using System;
using System.Collections;
using System.Text;
using UnityEngine;
using NativeWebSocket;

public class CameraToROS_RawJSON : MonoBehaviour
{
    [Header("Camera Settings")]
    public Camera mainCamera;              // 用于显示的主摄像机（保持可见）
    public int captureWidth = 640;         // 分辨率（可调，越大越慢）
    public int captureHeight = 480;
    public float sendRate = 0.5f;          // 秒，每帧发送间隔（默认 0.5s，测试用），建议 >=0.1f

    [Header("ROS Settings")]
    public string rosbridgeUrl = "ws://172.25.243.23:9090"; // 替换为你的 WSL2 IP
    public string topicName = "/image_raw";

    private WebSocket websocket;
    private Camera captureCamera;
    private RenderTexture renderTexture;
    private Texture2D texture2D;
    private bool isConnected = false;
    private bool isSending = false;

    async void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("[ROS] No mainCamera assigned and Camera.main is null.");
            return;
        }

        // 创建隐藏摄像机用于抓图（避免把主摄像机 targetTexture 改掉）
        GameObject camObj = new GameObject("ROS_CaptureCamera");
        camObj.hideFlags = HideFlags.HideAndDontSave;
        captureCamera = camObj.AddComponent<Camera>();
        captureCamera.CopyFrom(mainCamera);
        captureCamera.enabled = false; // 不直接渲染到 Game 窗口

        // RenderTexture 与 Texture2D
        renderTexture = new RenderTexture(captureWidth, captureHeight, 24);
        captureCamera.targetTexture = renderTexture;
        texture2D = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);

        // 初始化 WebSocket
        websocket = new WebSocket(rosbridgeUrl);

        websocket.OnOpen += () =>
        {
            Debug.Log("[ROS] Connected to rosbridge at " + rosbridgeUrl);
            isConnected = true;

            // advertise /image_raw
            string advertiseJson = "{\"op\":\"advertise\",\"topic\":\"" + topicName + "\",\"type\":\"sensor_msgs/msg/Image\"}";
            websocket.SendText(advertiseJson);
            Debug.Log("[ROS] Sent advertise for " + topicName);

            // 启动发送协程（确保只启动一次）
            if (!isSending) StartCoroutine(SendCameraFrames());
        };

        websocket.OnError += (e) =>
        {
            Debug.LogError("[ROS] WebSocket Error: " + e);
        };

        websocket.OnClose += (e) =>
        {
            Debug.LogWarning("[ROS] WebSocket Closed");
            isConnected = false;
            isSending = false;
        };

        try
        {
            await websocket.Connect();
        }
        catch (Exception ex)
        {
            Debug.LogError("[ROS] WebSocket Connect exception: " + ex);
        }
    }

    IEnumerator SendCameraFrames()
    {
        isSending = true;

        // 为了性能，重复分配StringBuilder一次（Clear后重用）
        StringBuilder sb = new StringBuilder(captureWidth * captureHeight * 3 / 2); // 预分配一个合理大小

        while (isConnected && this != null)
        {
            yield return new WaitForSeconds(sendRate);

            // 手动渲染到 RenderTexture（使用隐藏摄像机）
            captureCamera.transform.position = mainCamera.transform.position;
            captureCamera.transform.rotation = mainCamera.transform.rotation;
            captureCamera.fieldOfView = mainCamera.fieldOfView;
            captureCamera.orthographic = mainCamera.orthographic;
            captureCamera.orthographicSize = mainCamera.orthographicSize;
            captureCamera.Render();

            RenderTexture.active = renderTexture;
            texture2D.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
            texture2D.Apply();
            RenderTexture.active = null;

            byte[] rawBytes = texture2D.GetRawTextureData(); // RGB24: length = w*h*3

            // ========== 新增：垂直翻转像素数组（仅修改这部分）==========
            int rowBytes = captureWidth * 3; // 每行的字节数（RGB24：宽×3）
            byte[] flippedBytes = new byte[rawBytes.Length]; // 存储翻转后的字节
            // 从最后一行开始，逐行复制到新数组
            for (int row = 0; row < captureHeight; row++)
            {
                int srcStart = row * rowBytes;
                int dstStart = (captureHeight - 1 - row) * rowBytes;
                Array.Copy(rawBytes, srcStart, flippedBytes, dstStart, rowBytes);
            }
            rawBytes = flippedBytes; // 替换为翻转后的数组
            // ========== 翻转逻辑结束 ==========

            // 拼接 JSON 的 data 数组（uint8[]）为逗号分隔的数字
            sb.Clear();
            for (int i = 0; i < rawBytes.Length; i++)
            {
                sb.Append(rawBytes[i]);
                if (i < rawBytes.Length - 1) sb.Append(',');
            }
            string dataArrayText = sb.ToString();

            // 构建完整 publish JSON（手工拼接以避免 JsonUtility 对大数组的限制）
            long sec = DateTimeOffset.Now.ToUnixTimeSeconds();
            string publishJson =
                "{\"op\":\"publish\",\"topic\":\"" + topicName + "\",\"msg\":{" +
                  "\"header\":{\"stamp\":{\"sec\":" + sec + ",\"nanosec\":0},\"frame_id\":\"camera\"}," +
                  "\"height\":" + captureHeight + "," +
                  "\"width\":" + captureWidth + "," +
                  "\"encoding\":\"rgb8\"," +
                  "\"is_bigendian\":0," +
                  "\"step\":" + (captureWidth * 3) + "," +
                  "\"data\":[" + dataArrayText + "]" +
                "}}";

            // 发送（try-catch 避免某些错误中断协程）
            try
            {
                websocket.SendText(publishJson);
                Debug.Log("[ROS] Sent raw frame: bytes=" + rawBytes.Length + " json_size=" + publishJson.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ROS] Send exception: " + ex);
            }
        }

        isSending = false;
    }

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (websocket != null) websocket.DispatchMessageQueue();
#endif
    }

    private async void OnApplicationQuit()
    {
        if (websocket != null)
        {
            // optional: unadvertise
            string unadv = "{\"op\":\"unadvertise\",\"topic\":\"" + topicName + "\"}";
            websocket.SendText(unadv);

            await websocket.Close();
        }

        // 清理创建的对象
        if (captureCamera != null)
        {
            DestroyImmediate(captureCamera.gameObject);
        }
        if (renderTexture != null)
        {
            renderTexture.Release();
            DestroyImmediate(renderTexture);
        }
        if (texture2D != null)
        {
            DestroyImmediate(texture2D);
        }
    }
}
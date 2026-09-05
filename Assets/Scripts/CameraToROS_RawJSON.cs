using System;
using System.Collections;
using UnityEngine;
using NativeWebSocket;

public class CameraToROS_RawJSON : MonoBehaviour
{
    [Header("Camera Settings")]
    public Camera mainCamera;              // 用于显示的主摄像机（保持可见）
    public int captureWidth = 640;         // 分辨率（可调，越大越慢）
    public int captureHeight = 480;
    public float sendRate = 0.5f;          // 秒，每帧发送间隔。延迟敏感场景建议 0.1f (10Hz)，0.5f 只有 2Hz

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

        // 性能说明：
        // 图像 data 字段以 base64 字符串发送（rosbridge_suite 原生支持 uint8[] 的 base64 编码）。
        // 若逐字节转十进制逗号数组，640x480 会被膨胀成 ~3.2MB JSON，
        // Unity 主线程 92 万次拼接 + rosbridge 解析 92 万个 int token 都极慢，是延迟的元凶。
        int frameCount = 0;

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

            // ========== 垂直翻转像素数组（ROS 图像行序 top-down，ReadPixels 为 bottom-up）==========
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

            // base64 编码 data（一次调用替代 92 万次逐字节拼接）
            string dataArrayText = Convert.ToBase64String(rawBytes);

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
                  "\"data\":\"" + dataArrayText + "\"" +
                "}}";

            // 发送（try-catch 避免某些错误中断协程）；日志降频，避免每帧刷屏拖慢主线程
            try
            {
                websocket.SendText(publishJson);
                frameCount++;
                if ((frameCount % 100) == 0)
                    Debug.Log("[ROS] Sent raw frame #" + frameCount + ": bytes=" + rawBytes.Length + " json_len=" + publishJson.Length);
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

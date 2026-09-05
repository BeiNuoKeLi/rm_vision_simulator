using System;
using System.Collections;
using System.Text;
using System.Globalization;
using UnityEngine;
using NativeWebSocket;

public class CameraInfoAndRaw : MonoBehaviour
{
    [Header("Camera Settings")]
    public Camera mainCamera;              // 用于显示的主摄像机（保持可见）
    public int captureWidth = 640;         // 分辨率（可调，越大越慢）
    public int captureHeight = 480;
    public float sendRate = 0.5f;          // 秒，每帧发送间隔。延迟敏感场景建议 0.1f (10Hz)，0.5f 只有 2Hz

    [Header("ROS Settings")]
    public string rosbridgeUrl = "ws://172.25.243.23:9090"; // WSL2 IP
    public string topicName = "/image_raw";
    public string cameraInfoTopic = "/camera_info";

    [Header("CameraInfo Settings")]
    [Tooltip("If true, fx/fy/cx/cy will be computed from Unity Camera.fieldOfView and capture resolution. If false, use the explicit fx/fy/cx/cy values below.")]
    public bool useAutoIntrinsics = true;
    [Tooltip("Focal length in pixels (x). Only used if useAutoIntrinsics == false.")]
    public float fx = 500f;
    [Tooltip("Focal length in pixels (y). Only used if useAutoIntrinsics == false.")]
    public float fy = 500f;
    [Tooltip("Principal point x coordinate in pixels.")]
    public float cx = 320f;
    [Tooltip("Principal point y coordinate in pixels.")]
    public float cy = 240f;
    [Tooltip("Distortion coefficients (k1,k2,t1,t2,k3) or fewer/more if you set a different length.")]
    public float[] distortionCoeffs = new float[5] { 0f, 0f, 0f, 0f, 0f };
    [Tooltip("Distortion model string (commonly 'plumb_bob' or 'rational_polynomial').")]
    public string distortionModel = "plumb_bob";

    [Tooltip("If true, camera_info JSON will be published for every image frame. If false, camera_info will be published once on connect.")]
    public bool sendCameraInfoEachFrame = true;

    private WebSocket websocket;
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

        // 使用主摄像机的 RenderTexture
        renderTexture = new RenderTexture(captureWidth, captureHeight, 24);
        mainCamera.targetTexture = renderTexture;
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

            // advertise /camera_info
            string advertiseCamInfo = "{\"op\":\"advertise\",\"topic\":\"" + cameraInfoTopic + "\",\"type\":\"sensor_msgs/msg/CameraInfo\"}";
            websocket.SendText(advertiseCamInfo);
            Debug.Log("[ROS] Sent advertise for " + cameraInfoTopic);

            // publish camera_info once on connect (unless sendCameraInfoEachFrame is true, then it will be sent with each frame)
            if (!sendCameraInfoEachFrame)
            {
                long sec = DateTimeOffset.Now.ToUnixTimeSeconds();
                uint nanosec = (uint)(DateTimeOffset.Now.ToUnixTimeMilliseconds() % 1000) * 1000000u;
                string camInfoJson = BuildCameraInfoJson(sec, nanosec);
                websocket.SendText(camInfoJson);
                Debug.Log("[ROS] Published camera_info (initial).");
            }

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
        // 图像 data 字段以 base64 字符串发送（rosbridge_suite 原生支持 uint8[] 的 base64 编码，
        // 其出站序列化也是 base64）。若逐字节转十进制逗号数组，640x480 会被膨胀成 ~3.2MB JSON，
        // Unity 主线程 92 万次 ToString + rosbridge 解析 92 万个 int token 都极慢，是延迟的元凶。
        int frameCount = 0;

        while (isConnected && this != null)
        {
            yield return new WaitForSeconds(sendRate);

            // 直接渲染主摄像机的图像到 RenderTexture
            mainCamera.Render();

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

            // base64 编码 data（一次调用替代 92 万次 ToString + 逗号拼接）
            string dataArrayText = Convert.ToBase64String(rawBytes);

            // 构建完整 publish JSON（手工拼接以避免 JsonUtility 对大数组的限制）
            long sec = DateTimeOffset.Now.ToUnixTimeSeconds();
            uint nanosec = (uint)(DateTimeOffset.Now.ToUnixTimeMilliseconds() % 1000) * 1000000u;
            string publishJson =
                "{\"op\":\"publish\",\"topic\":\"" + topicName + "\",\"msg\":{" +
                  "\"header\":{\"stamp\":{\"sec\":" + sec + ",\"nanosec\":" + nanosec + "},\"frame_id\":\"camera_optical_frame\"}," +
                  "\"height\":" + captureHeight + "," +
                  "\"width\":" + captureWidth + "," +
                  "\"encoding\":\"rgb8\"," +
                  "\"is_bigendian\":0," +
                  "\"step\":" + (captureWidth * 3) + "," +
                  "\"data\":\"" + dataArrayText + "\"" +
                "}}";

            // 发送 camera_info 每帧（如果用户选择这样）
            if (sendCameraInfoEachFrame)
            {
                string camInfoJson = BuildCameraInfoJson(sec, nanosec);
                try
                {
                    websocket.SendText(camInfoJson);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[ROS] Send camera_info exception: " + ex);
                }
            }

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

    /// <summary>
    /// 构建 sensor_msgs/CameraInfo JSON（rosbridge 格式）
    /// header.stamp 使用传入的 sec/nanosec
    /// k: 3x3 row-major
    /// r: 3x3 row-major (identity)
    /// p: 3x4 row-major (projection matrix)
    /// d: distortion coeffs array
    /// </summary>
    private string BuildCameraInfoJson(long sec, uint nanosec)
    {
        // 计算/确定内参
        float useFx = fx;
        float useFy = fy;
        float useCx = cx;
        float useCy = cy;

        if (useAutoIntrinsics && mainCamera != null)
        {
            // Unity Camera.fieldOfView 是垂直 FOV（度）
            float vFovRad = mainCamera.fieldOfView * Mathf.Deg2Rad;
            // focal in pixels: f = (height/2) / tan(vFov/2)
            useFy = (captureHeight / 2f) / Mathf.Tan(vFovRad / 2f);
            // fx scales with aspect ratio: fx = fy * (width/height)
            useFx = useFy * ((float)captureWidth / (float)captureHeight);
            useCx = captureWidth / 2f;
            useCy = captureHeight / 2f;
        }

        // Prepare K (row-major 3x3)
        // [fx, 0, cx, 0, fy, cy, 0, 0, 1]
        string k_array =
            useFx.ToString(CultureInfo.InvariantCulture) + ",0," + useCx.ToString(CultureInfo.InvariantCulture) + "," +
            "0," + useFy.ToString(CultureInfo.InvariantCulture) + "," + useCy.ToString(CultureInfo.InvariantCulture) + "," +
            "0,0,1";

        // R (identity)
        string r_array = "1,0,0,0,1,0,0,0,1";

        // P (3x4): [fx 0 cx 0, 0 fy cy 0, 0 0 1 0]
        string p_array =
            useFx.ToString(CultureInfo.InvariantCulture) + ",0," + useCx.ToString(CultureInfo.InvariantCulture) + ",0," +
            "0," + useFy.ToString(CultureInfo.InvariantCulture) + "," + useCy.ToString(CultureInfo.InvariantCulture) + ",0," +
            "0,0,1,0";

        // d (distortion) -> comma separated floats
        StringBuilder dSb = new StringBuilder(64);
        for (int i = 0; i < distortionCoeffs.Length; i++)
        {
            dSb.Append(distortionCoeffs[i].ToString(CultureInfo.InvariantCulture));
            if (i < distortionCoeffs.Length - 1) dSb.Append(',');
        }

        // Build JSON
        string camInfoJson =
       "{\"op\":\"publish\",\"topic\":\"" + cameraInfoTopic + "\",\"msg\":{" +
         "\"header\":{\"stamp\":{\"sec\":" + sec + ",\"nanosec\":" + nanosec + "},\"frame_id\":\"camera_optical_frame\"}," +
         "\"height\":" + captureHeight + "," +
         "\"width\":" + captureWidth + "," +
         "\"distortion_model\":\"" + distortionModel + "\"," +
         "\"d\":[" + dSb + "]," +
         "\"k\":[" + k_array + "]," +
         "\"r\":[" + r_array + "]," +
         "\"p\":[" + p_array + "]," +
         "\"binning_x\":0," +
         "\"binning_y\":0," +
         "\"roi\":{" +
           "\"x_offset\":0," +
           "\"y_offset\":0," +
           "\"height\":0," +
           "\"width\":0," +
           "\"do_rectify\":false" +
         "}" +
       "}}";


        return camInfoJson;
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
            string unadvCamInfo = "{\"op\":\"unadvertise\",\"topic\":\"" + cameraInfoTopic + "\"}";
            websocket.SendText(unadvCamInfo);

            await websocket.Close();
        }

        // 清理创建的对象
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

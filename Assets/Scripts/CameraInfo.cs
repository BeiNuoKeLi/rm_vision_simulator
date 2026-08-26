using System;
using System.Collections;
using System.Text;
using UnityEngine;
using NativeWebSocket;

/// <summary>
/// 独立的Unity相机内参（CameraInfo）向ROS发送脚本（兼容所有Unity版本）
/// 依赖：NativeWebSocket插件（需提前导入Unity）
/// </summary>
public class CameraInfo : MonoBehaviour
{
    [Header("相机参数")]
    public Camera targetCamera; // 要发送内参的相机（拖入场景中的相机）
    public int imageWidth = 640; // 图像宽度（需和ROS端图像分辨率一致）
    public int imageHeight = 480; // 图像高度

    [Header("ROS连接参数")]
    public string rosbridgeWsUrl = "ws://172.25.243.23:9090"; // ROSBridge的WebSocket地址
    public string cameraInfoTopic = "/camera_info"; // 要发布的CameraInfo话题名
    public float sendInterval = 0.5f; // 发送间隔（秒，建议≥0.1）

    // ROS CameraInfo核心参数
    private double[] K; // 内参矩阵 3x3
    private double[] R; // 旋转矩阵 3x3（单位矩阵）
    private double[] P; // 投影矩阵 3x4
    private double[] D; // 畸变系数（无畸变则为空）

    private WebSocket ws;
    private bool isWsConnected = false;
    private bool isSending = false;

    // 时间戳基准（Unix纪元：1970-01-01 00:00:00 UTC）
    private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    void Start()
    {
        // 1. 校验必要参数
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
            if (targetCamera == null)
            {
                Debug.LogError("[CameraInfoToROS] 未指定目标相机，且场景中无主相机！");
                enabled = false;
                return;
            }
            Debug.LogWarning("[CameraInfoToROS] 未指定目标相机，自动使用主相机");
        }

        // 2. 计算相机内参（仅初始化时计算一次，若相机参数动态变化需手动更新）
        CalculateCameraIntrinsics();

        // 3. 初始化WebSocket并连接ROSBridge
        InitWebSocket();
    }

    /// <summary>
    /// 计算Unity相机的ROS内参（K/R/P矩阵）
    /// </summary>
    private void CalculateCameraIntrinsics()
    {
        // 畸变系数：Unity默认无畸变，为空数组
        D = new double[0];

        // 旋转矩阵：单位矩阵（无旋转）
        R = new double[9] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        // 计算内参矩阵K（核心）
        float fovRad = targetCamera.fieldOfView * Mathf.Deg2Rad; // 垂直视场角转弧度
        float fx = (imageWidth / 2f) / Mathf.Tan(fovRad / 2f); // 水平焦距
        float fy = fx * (imageHeight / (float)imageWidth); // 垂直焦距（适配宽高比）
        float cx = imageWidth / 2f; // 主点X
        float cy = imageHeight / 2f; // 主点Y

        K = new double[9]
        {
            fx, 0, cx,
            0, fy, cy,
            0, 0, 1
        };

        // 投影矩阵P（3x4，前3列=K，第4列=[0,0,0]）
        P = new double[12]
        {
            fx, 0, cx, 0,
            0, fy, cy, 0,
            0, 0, 1, 0
        };

        Debug.Log($"[CameraInfoToROS] 计算完成内参：fx={fx:F2}, fy={fy:F2}, cx={cx}, cy={cy}");
    }

    /// <summary>
    /// 初始化WebSocket并连接ROSBridge
    /// </summary>
    private async void InitWebSocket()
    {
        try
        {
            ws = new WebSocket(rosbridgeWsUrl);

            // WebSocket回调
            ws.OnOpen += OnWsConnected;
            ws.OnError += (error) => Debug.LogError($"[CameraInfoToROS] WebSocket错误：{error}");
            ws.OnClose += async (code) =>
            {
                Debug.LogWarning($"[CameraInfoToROS] WebSocket断开（码：{code}）");
                isWsConnected = false;
                isSending = false;

                // 兜底：断开时发送unadvertise
                if (ws != null)
                {
                    try
                    {
                        string unadvertiseMsg = $"{{\"op\":\"unadvertise\",\"topic\":\"{cameraInfoTopic}\"}}";
                        await ws.SendText(unadvertiseMsg);
                        Debug.Log("[CameraInfoToROS] 断开时已发送unadvertise指令");
                    }
                    catch { }
                }
            };

            // 连接ROSBridge
            await ws.Connect();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CameraInfoToROS] 连接ROSBridge失败：{ex.Message}");
        }
    }

    /// <summary>
    /// WebSocket连接成功回调
    /// </summary>
    private void OnWsConnected()
    {
        Debug.Log($"[CameraInfoToROS] 已连接ROSBridge：{rosbridgeWsUrl}");
        isWsConnected = true;

        // 声明CameraInfo话题（修复：type改为小写camera_info）
        string advertiseMsg = $"{{\"op\":\"advertise\",\"topic\":\"{cameraInfoTopic}\",\"type\":\"sensor_msgs/msg/CameraInfo\"}}";
        ws.SendText(advertiseMsg);
        Debug.Log($"[CameraInfoToROS] 已声明话题：{cameraInfoTopic}");

        // 启动定时发送协程
        if (!isSending)
        {
            StartCoroutine(SendCameraInfoLoop());
        }
    }

    /// <summary>
    /// 循环发送CameraInfo消息
    /// </summary>
    private IEnumerator SendCameraInfoLoop()
    {
        isSending = true;
        StringBuilder sb = new StringBuilder(); // 复用StringBuilder提升性能

        while (isWsConnected && gameObject.activeInHierarchy)
        {
            yield return new WaitForSeconds(sendInterval);

            // 兼容所有Unity版本的时间戳计算（秒 + 纳秒）
            long[] timestamp = GetUnixTimestamp();
            long timestampSec = timestamp[0];
            long timestampNano = timestamp[1];

            // 构建CameraInfo的JSON消息
            string camInfoMsg = BuildCameraInfoJson(timestampSec, timestampNano, sb);

            // 发送消息
            try
            {
                ws.SendText(camInfoMsg);
                Debug.Log($"[CameraInfoToROS] 已发送CameraInfo（时间戳：{timestampSec}.{timestampNano}）");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CameraInfoToROS] 发送失败：{ex.Message}");
            }
        }

        isSending = false;
    }

    /// <summary>
    /// 兼容所有Unity版本的Unix时间戳计算（返回：[秒, 纳秒]）
    /// 核心：用Ticks计算（1 Tick = 100纳秒，所有Unity版本都支持）
    /// </summary>
    private long[] GetUnixTimestamp()
    {
        // 获取当前UTC时间到Unix纪元的时间差
        TimeSpan timeSinceEpoch = DateTime.UtcNow - epoch;

        // 计算总秒数（整数部分）
        long sec = (long)timeSinceEpoch.TotalSeconds;

        // 计算纳秒：1 Tick = 100纳秒 → 总纳秒 = Ticks * 100
        // 取纳秒的后9位（避免超过纳秒范围）
        long totalNano = timeSinceEpoch.Ticks * 100;
        long nano = totalNano % 1000000000;

        return new long[] { sec, nano };
    }

    /// <summary>
    /// 构建CameraInfo的JSON字符串（修复：字段D→d、K→k、R→r、P→p）
    /// </summary>
    private string BuildCameraInfoJson(long sec, long nano, StringBuilder sb)
    {
        // 辅助函数：将double数组转为逗号分隔的字符串
        Func<double[], string> array2Str = (arr) =>
        {
            sb.Clear();
            for (int i = 0; i < arr.Length; i++)
            {
                sb.Append(arr[i].ToString("0.000000")); // 保留6位小数
                if (i < arr.Length - 1) sb.Append(',');
            }
            return sb.ToString();
        };

        // 拼接JSON（严格符合ROS2的sensor_msgs/msg/CameraInfo格式）
        return $"{{" +
               $"\"op\":\"publish\"," +
               $"\"topic\":\"{cameraInfoTopic}\"," +
               $"\"msg\":{{" +
                   $"\"header\":{{" +
                       $"\"stamp\":{{\"sec\":{sec},\"nanosec\":{nano}}}," +
                       $"\"frame_id\":\"camera\"" + // 帧ID需和Image消息保持一致
                   $"}}," +
                   $"\"height\":{imageHeight}," +
                   $"\"width\":{imageWidth}," +
                   $"\"distortion_model\":\"\" ," + // 无畸变则为空
                   $"\"d\":[{array2Str(D)}]," + // 修复：D→d
                   $"\"k\":[{array2Str(K)}]," + // 修复：K→k
                   $"\"r\":[{array2Str(R)}]," + // 修复：R→r
                   $"\"p\":[{array2Str(P)}]," + // 修复：P→p
                   $"\"binning_x\":0," +
                   $"\"binning_y\":0," +
                   $"\"roi\":{{\"x_offset\":0,\"y_offset\":0,\"height\":0,\"width\":0,\"do_rectify\":false}}" +
               $"}}" +
           $"}}";
    }

    void Update()
    {
        // 处理WebSocket消息队列（非WebGL平台必需）
#if !UNITY_WEBGL || UNITY_EDITOR
        if (ws != null)
        {
            ws.DispatchMessageQueue();
        }
#endif
    }

    /// <summary>
    /// 脚本禁用时执行（Unity停止运行时必触发，兜底发送unadvertise）
    /// </summary>
    private async void OnDisable()
    {
        if (ws != null && isWsConnected)
        {
            try
            {
                string unadvertiseMsg = $"{{\"op\":\"unadvertise\",\"topic\":\"{cameraInfoTopic}\"}}";
                await ws.SendText(unadvertiseMsg);
                Debug.Log("[CameraInfoToROS] 脚本禁用时已发送unadvertise指令");

                await ws.Close();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CameraInfoToROS] 发送unadvertise失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 应用退出时清理资源
    /// </summary>
    private async void OnApplicationQuit()
    {
        if (ws != null && isWsConnected)
        {
            // 取消话题声明
            string unadvertiseMsg = $"{{\"op\":\"unadvertise\",\"topic\":\"{cameraInfoTopic}\"}}";
            await ws.SendText(unadvertiseMsg);

            // 关闭WebSocket
            await ws.Close();
            Debug.Log("[CameraInfoToROS] 应用退出时已关闭WebSocket连接");
        }
    }
}
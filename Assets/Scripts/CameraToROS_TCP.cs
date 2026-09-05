using System;
using System.Collections;
using System.Text;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// 高性能图像通道：把 Unity 相机画面通过 TCP 以二进制 raw RGB 直发 WSL 收图节点，
/// 再由收图节点发布为 ROS2 /image_raw + /camera_info。
/// 帧格式（little-endian）：[4B header长度][header JSON][w*h*3 原始RGB字节]
///
/// 渲染路径与旧 CameraInfoAndRaw 保持一致：直接渲染【主相机】到 RenderTexture，
/// 以保留主相机上的全部图层/后处理/脚本（复制相机无法带出这些内容，会导致数字等叠加层丢失）。
/// </summary>
public class CameraToROS_TCP : MonoBehaviour
{
    [Header("Camera Settings")]
    public Camera mainCamera;              // 主摄像机（保持可见）
    public int captureWidth = 640;
    public int captureHeight = 480;
    [Tooltip("帧间隔（秒）。0.0667 ≈ 15fps，0.1 ≈ 10fps")]
    public float sendRate = 0.0667f;

    [Header("TCP Settings (WSL 收图节点)")]
    public string serverHost = "172.25.243.23"; // WSL2 IP
    public int serverPort = 10001;

    [Header("CameraInfo")]
    public bool useAutoIntrinsics = true;
    public float fx = 500f;
    public float fy = 500f;
    public float cx = 320f;
    public float cy = 240f;
    public string distortionModel = "plumb_bob";

    private RenderTexture renderTexture;
    private Texture2D texture2D;
    private TcpClient client;
    private NetworkStream stream;
    private volatile bool tcpConnected = false;
    private bool isSending = false;
    private string camInfoFields = "{}"; // 缓存的 camera_info 内层 JSON（不含两端大括号）

    void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("[ROS-TCP] No mainCamera assigned and Camera.main is null.");
            return;
        }

        // 与旧 CameraInfoAndRaw 相同的渲染路径：直接渲染主相机到 RT
        renderTexture = new RenderTexture(captureWidth, captureHeight, 24);
        texture2D = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);

        ComputeCameraInfoFields();
        StartCoroutine(ConnectLoop());
    }

    /// <summary>计算并缓存 camera_info 内层 JSON（k/r/p/d）</summary>
    private void ComputeCameraInfoFields()
    {
        float useFx = fx, useFy = fy, useCx = cx, useCy = cy;
        if (useAutoIntrinsics && mainCamera != null)
        {
            float vFovRad = mainCamera.fieldOfView * Mathf.Deg2Rad;
            useFy = (captureHeight / 2f) / Mathf.Tan(vFovRad / 2f);
            useFx = useFy * ((float)captureWidth / (float)captureHeight);
            useCx = captureWidth / 2f;
            useCy = captureHeight / 2f;
        }
        string f2 = "0.###";
        string kStr = useFx.ToString(f2) + ",0," + useCx.ToString(f2) + "," +
                      "0," + useFy.ToString(f2) + "," + useCy.ToString(f2) + ",0,0,1";
        string pStr = useFx.ToString(f2) + ",0," + useCx.ToString(f2) + ",0," +
                      "0," + useFy.ToString(f2) + "," + useCy.ToString(f2) + ",0,0,0,1,0";

        camInfoFields =
            "\"distortion_model\":\"" + distortionModel + "\"," +
            "\"d\":[0,0,0,0,0]," +
            "\"k\":[" + kStr + "]," +
            "\"r\":[1,0,0,0,1,0,0,0,1]," +
            "\"p\":[" + pStr + "]";
        Debug.Log("[ROS-TCP] camera_info fields cached");
    }

    /// <summary>连接循环：断线自动重连，连接成功后启动发帧协程。
    /// 注意：C# 不允许在带 catch 的 try 块内 yield，因此把“创建连接(可能抛异常)”与“等待连接完成(yield)”拆成两段。</summary>
    IEnumerator ConnectLoop()
    {
        while (this != null)
        {
            // 等一段再连，避免忙转
            yield return new WaitForSecondsRealtime(1f);
            if (tcpConnected) yield break;

            // ---- 阶段 1：创建并发起连接（无 yield，异常可在此捕获）----
            var task = default(System.Threading.Tasks.Task);
            try
            {
                client = new TcpClient();
                client.NoDelay = true;
                task = client.ConnectAsync(serverHost, serverPort);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ROS-TCP] connect failed: " + ex.Message);
                if (client != null) { try { client.Close(); } catch { } }
                client = null;
                stream = null;
                continue;
            }

            // ---- 阶段 2：轮询等待连接完成（yield 在 try 之外）----
            float deadline = Time.realtimeSinceStartup + 3f;
            while (task != null && !task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    // 超时：主动关闭使异步连接中止
                    try { if (client != null) client.Close(); } catch { }
                    break;
                }
                yield return null;
            }

            if (task == null || task.IsFaulted || client == null || !client.Connected)
            {
                Debug.LogWarning("[ROS-TCP] connect timeout or failed, retry later");
                try { if (client != null) client.Close(); } catch { }
                client = null;
                stream = null;
                continue;
            }

            stream = client.GetStream();
            tcpConnected = true;
            Debug.Log("[ROS-TCP] connected to " + serverHost + ":" + serverPort);
            StartCoroutine(SendFrames());
            // 等待断开或停止
            while (tcpConnected && this != null) yield return null;
        }
    }

    IEnumerator SendFrames()
    {
        isSending = true;
        int frameCount = 0;
        StringBuilder sbHeader = new StringBuilder(1024);

        while (tcpConnected && isConnectedOk() && this != null)
        {
            yield return new WaitForSeconds(sendRate);

            // 与旧 CameraInfoAndRaw 相同的“主相机直渲”：渲染的是主相机完整画面
            //（含其 GameObject 上的后处理/叠加层/所有图层，数字等元素不会丢）
            mainCamera.targetTexture = renderTexture;
            mainCamera.Render();

            RenderTexture.active = renderTexture;
            texture2D.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
            texture2D.Apply();
            RenderTexture.active = null;

            // 立即还原，避免 Game 窗口被占用而黑屏
            mainCamera.targetTexture = null;

            byte[] rawBytes = texture2D.GetRawTextureData(); // RGB24 w*h*3

            // 垂直翻转（ReadPixels 行序 bottom-up，ROS 需要 top-down）
            int rowBytes = captureWidth * 3;
            byte[] flipped = new byte[rawBytes.Length];
            for (int row = 0; row < captureHeight; row++)
            {
                int srcStart = row * rowBytes;
                int dstStart = (captureHeight - 1 - row) * rowBytes;
                Array.Copy(rawBytes, srcStart, flipped, dstStart, rowBytes);
            }
            rawBytes = flipped;

            // 帧头 JSON
            long sec = DateTimeOffset.Now.ToUnixTimeSeconds();
            long nanosec = (DateTimeOffset.Now.ToUnixTimeMilliseconds() % 1000) * 1000000L;
            sbHeader.Length = 0;
            sbHeader.Append("{\"w\":").Append(captureWidth)
                    .Append(",\"h\":").Append(captureHeight)
                    .Append(",\"step\":").Append(captureWidth * 3)
                    .Append(",\"enc\":\"rgb8\"")
                    .Append(",\"frame_id\":\"camera_optical_frame\"")
                    .Append(",\"sec\":").Append(sec)
                    .Append(",\"nsec\":").Append(nanosec)
                    .Append(",\"cam_info\":{").Append(camInfoFields).Append("}}");
            byte[] headerBytes = Encoding.UTF8.GetBytes(sbHeader.ToString());
            byte[] lenBytes = BitConverter.GetBytes(headerBytes.Length); // little-endian

            // 组包一次性发送
            byte[] packet = new byte[4 + headerBytes.Length + rawBytes.Length];
            Buffer.BlockCopy(lenBytes, 0, packet, 0, 4);
            Buffer.BlockCopy(headerBytes, 0, packet, 4, headerBytes.Length);
            Buffer.BlockCopy(rawBytes, 0, packet, 4 + headerBytes.Length, rawBytes.Length);

            try
            {
                stream.Write(packet, 0, packet.Length);
                stream.Flush();
                frameCount++;
                if (frameCount % 150 == 0)
                    Debug.Log("[ROS-TCP] sent frame #" + frameCount + " bytes=" + rawBytes.Length);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ROS-TCP] send failed, will reconnect: " + ex.Message);
                MarkDisconnected();
            }
        }
        isSending = false;
    }

    private bool isConnectedOk()
    {
        try { return client != null && client.Connected; }
        catch { return false; }
    }

    private void MarkDisconnected()
    {
        tcpConnected = false;
        try { if (stream != null) stream.Close(); } catch { }
        try { if (client != null) client.Close(); } catch { }
        client = null; stream = null;
    }

    private void OnDestroy()
    {
        MarkDisconnected();
        if (mainCamera != null) mainCamera.targetTexture = null; // 保险还原
        if (renderTexture != null) { renderTexture.Release(); DestroyImmediate(renderTexture); }
        if (texture2D != null) DestroyImmediate(texture2D);
    }
}

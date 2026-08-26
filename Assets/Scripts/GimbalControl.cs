using System;
using System.Collections;
using System.Text;
using UnityEngine;
using NativeWebSocket;

public class GimbalControl : MonoBehaviour
{
    public float max_pitch_angle = 35;
    public float min_pitch_angle = -25;
    public bool enable_mouse_control = true;

    [Header("ROS连接配置")]
    public string rosbridgeWsUrl = "ws://172.25.243.23:9090";
    public string jointStateTopic = "/joint_states";

    private Transform pitch_transform;
    private Transform yaw_transform;

    private WebSocket ws;
    private bool isWsConnected = false;

    private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    void Start()
    {
        var yaw_link = this.name + "/base_link/yaw_link";
        var pitch_link = yaw_link + "/pitch_link";
        pitch_transform = GameObject.Find(pitch_link).transform;
        yaw_transform = GameObject.Find(yaw_link).transform;

        Cursor.lockState = CursorLockMode.Locked;

        InitWebSocket();
    }

    private async void InitWebSocket()
    {
        try
        {
            ws = new WebSocket(rosbridgeWsUrl);
            ws.OnOpen += OnWsConnected;
            ws.OnError += (error) => Debug.LogError($"[GimbalControl] WebSocket错误: {error}");
            ws.OnClose += async (code) =>
            {
                Debug.LogWarning($"[GimbalControl] WebSocket断开: {code}");
                isWsConnected = false;
            };
            await ws.Connect();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GimbalControl] 连接ROSBridge失败: {ex.Message}");
        }
    }

    private void OnWsConnected()
    {
        Debug.Log($"[GimbalControl] 已连接ROSBridge: {rosbridgeWsUrl}");
        isWsConnected = true;

        string advertiseMsg = $"{{\"op\":\"advertise\",\"topic\":\"{jointStateTopic}\",\"type\":\"sensor_msgs/msg/JointState\"}}";
        ws.SendText(advertiseMsg);
        Debug.Log($"[GimbalControl] 已发布话题: {jointStateTopic}");
    }

    void FixedUpdate()
    {
        if (Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt))
        {
            enable_mouse_control = !enable_mouse_control;
            Cursor.lockState = enable_mouse_control ? CursorLockMode.Locked : CursorLockMode.None;
        }

        if (enable_mouse_control)
        {
            var mouse_x = Input.GetAxis("Mouse X");
            var mouse_y = Input.GetAxis("Mouse Y");
            yaw_transform.Rotate(0, mouse_x, 0);
            pitch_transform.Rotate(-mouse_y, 0, 0);

            var pitch_angle = pitch_transform.localEulerAngles.x;
            if (pitch_angle > 180)
            {
                pitch_angle -= 360;
            }
            pitch_angle = Mathf.Clamp(pitch_angle, min_pitch_angle, max_pitch_angle);
            pitch_transform.localEulerAngles = new Vector3(pitch_angle, 180, 0);
        }

        if (isWsConnected && ws != null)
        {
            TimeSpan timeSinceEpoch = DateTime.UtcNow - epoch;
            long sec = (long)timeSinceEpoch.TotalSeconds;
            long nano = (timeSinceEpoch.Ticks * 100) % 1000000000;

            double yaw = -yaw_transform.localEulerAngles.y / 180.0 * Math.PI;
            double pitch = pitch_transform.localEulerAngles.x / 180.0 * Math.PI;

            string msg = $"{{" +
                $"\"op\":\"publish\"," +
                $"\"topic\":\"{jointStateTopic}\"," +
                $"\"msg\":{{" +
                    $"\"header\":{{\"stamp\":{{\"sec\":{sec},\"nanosec\":{nano}}},\"frame_id\":\"\"}}," +
                    $"\"name\":[\"yaw_joint\",\"pitch_joint\"]," +
                    $"\"position\":[{yaw.ToString("F6")},{pitch.ToString("F6")}]" +
                $"}}" +
            $"}}";

            ws.SendText(msg);
        }
    }

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (ws != null)
        {
            ws.DispatchMessageQueue();
        }
#endif
    }

    private async void OnDisable()
    {
        if (ws != null && isWsConnected)
        {
            try
            {
                string unadvertiseMsg = $"{{\"op\":\"unadvertise\",\"topic\":\"{jointStateTopic}\"}}";
                await ws.SendText(unadvertiseMsg);
                await ws.Close();
            }
            catch { }
        }
    }

    private async void OnApplicationQuit()
    {
        if (ws != null && isWsConnected)
        {
            string unadvertiseMsg = $"{{\"op\":\"unadvertise\",\"topic\":\"{jointStateTopic}\"}}";
            await ws.SendText(unadvertiseMsg);
            await ws.Close();
        }
    }
}

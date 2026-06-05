using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;
using ViveSR.anipal.Eye;
using ViveSR.anipal;
using ViveSR;
using UnityEngine.UI;
using System;
using System.Net.Sockets;
using Unity.VisualScripting;
using static TMPro.SpriteAssetUtilities.TexturePacker_JsonArray;
using System.IO;
using LSL;
using System.Threading;
using Valve.VR;
using UnityEngine.XR;
using System.Threading;
using System.Threading.Tasks;
using System.IO.MemoryMappedFiles;
using System.Linq;
//using Tobii.XR;

//using Valve.VR;
public class GazeRays : MonoBehaviour
{
    public GameObject objectExternalVideoLoader;
    private ExternalVideoLoader scriptExternalVideoLoader;

    // ********************************************************************************************************************
    //
    //  Parameters for eye data.
    //
    // ********************************************************************************************************************
    private static EyeData_v2 eyeData = new EyeData_v2();
    public EyeParameter eye_parameter = new EyeParameter();
    public GazeRayParameter gaze = new GazeRayParameter();
    private static bool eye_callback_registered = false;
    private static UInt64 eye_valid_L, eye_valid_R;                 // The bits explaining the validity of eye data.
    private static float openness_L, openness_R;                    // The level of eye openness.
    private static float pupil_diameter_L, pupil_diameter_R;        // Diameter of pupil dilation.
    private static Vector2 pos_sensor_L, pos_sensor_R;              // Positions of pupils.
    private static Vector3 gaze_origin_L, gaze_origin_R;            // Position of gaze origin.
    private static Vector3 gaze_direct_L, gaze_direct_R;            // Direction of gaze ray.
    private static float frown_L, frown_R;                          // The level of user's frown.
    private static float squeeze_L, squeeze_R;                      // The level to show how the eye is closed tightly.
    private static float wide_L, wide_R;                            // The level to show how the eye is open widely.
    private static double gaze_sensitive;                           // The sensitive factor of gaze ray.
    private static float distance_C;                                // Distance from the central point of right and left eyes.
    private static bool distance_valid_C;                           // Validity of combined data of right and left eyes.
    public bool cal_need;                                           // Calibration judge.
    public bool result_cal;                                         // Result of calibration.
    private static long MeasureTime, CurrentTime, MeasureEndTime = 0;
    private static float time_stamp;
    private static int frame;
    private static int track_imp_cnt = 0;
    private static TrackingImprovement[] track_imp_item;
    // ********************************************************************************************************************
    //
    //  Parameters for gaze.
    //
    // ********************************************************************************************************************
    private GameObject LeftVisual;
    private GameObject RightVisual;
    GameObject LeftSphere;
    GameObject RightSphere;
    private static Vector2 leftEyeScreenPoint;
    private static Vector2 rightEyeScreenPoint;

    // ********************************************************************************************************************
    //
    //  Parameters for debugging.
    //
    // ********************************************************************************************************************
    public Text eyeDataText;

    // ********************************************************************************************************************
    //
    //  Parameters for LSL and socket. Choose one from LSL and socket.
    //
    // ********************************************************************************************************************
    private TcpClient client;
    private static StreamWriter writer;
    private static int channelCount = 55;
    private float captureRate = 128;
    static float[] data = new float[55];
    private static StreamOutlet outlet;
    private static StreamInfo streamInfo;
    private Thread dataSendThread;
    private bool continueSending = true;
    private static List<float[]> dataQueue = new List<float[]>();
    private static readonly object dataLock = new object();

    private List<XRNodeState> nodeStates = new List<XRNodeState>();

    private static Vector3 eyePosition = Vector3.zero;
    private static Quaternion eyeRotation = Quaternion.identity;
    private static Vector3 headPosition = Vector3.zero;
    private static Quaternion headRotation = Quaternion.identity;
    private static Vector3 headVelocity = Vector3.zero;
    private static Vector3 headAngularVelocity = Vector3.zero;
    private static Vector3 tobiigazePoint = Vector3.zero;
    //private static Vector3 headeulerAngles = Vector3.zero; 
    private static bool isHMDConnected = false;
    private static bool isUserWearingHMD = false;
    private string lslUUID; // Variable to store the LSL UUID
    private const string defaultUUID = "mdsk23917"; // Default UUID
    public Camera externalCamera;
    private Vector2 vrResolution;
    private Vector2 externalResolution;
    private Vector2 vrCenter;
    private float scaleFactor;
    private Vector2 externalCenter;

    private static float[] leftEyeXArray = new float[5];
    private static float[] leftEyeYArray = new float[5];
    private static float[] rightEyeXArray = new float[5];
    private static float[] rightEyeYArray = new float[5];

    private static MemoryMappedFile mmf_eye_tracker;
    private static MemoryMappedViewAccessor accessor_eye_tracker;
    private static bool running = true;
    private static int callbackCounter = 0;
    private static int currentIndex = 0;
    private static bool loggedEyeTrackerMemoryWriteFailure = false;
    private static bool loggedLslPushFailure = false;
    private static readonly SRanipal_Eye_v2.CallbackBasic eyeCallbackDelegate = EyeCallback;
    private static readonly IntPtr eyeCallbackPtr = Marshal.GetFunctionPointerForDelegate(eyeCallbackDelegate);
    private static float lastEyeSampleRealtime = -1f;
    private static float lastRecoverAttemptRealtime = -10f;
    private static bool eyeStalled = false;
    private static bool loggedRecoverFailure = false;

    void HandleEyeCalibrationSignal()
    {
        Debug.Log("startCalibration Signal received in GazeRays.cs");
        SRanipal_Eye_v2.LaunchEyeCalibration();
    }

    void HandleEyeTrackerSignal(string sharedMemoryName)
    {
        InitializeMemoryMappedFile(sharedMemoryName);
    }

    void Start()
    {
        InitializeMemoryMappedFile(defaultUUID);
        SteamVR_Utils.Event.Send("reset_seated_pose"); // 触发SteamVR重新定位事件
        //XRSettings.gameViewRenderMode = GameViewRenderMode.BothEyes;
        // 获取VideoPlayerController.cs的脚本组件
        scriptExternalVideoLoader = objectExternalVideoLoader.GetComponent<ExternalVideoLoader>();
        
        // 订阅事件，这里是为了处理眼动校准的指令
        if (scriptExternalVideoLoader != null)
        {
            scriptExternalVideoLoader.OnCalibrationSignalSent += HandleEyeCalibrationSignal;
            scriptExternalVideoLoader.OnEyeTrackerSignalSent += HandleEyeTrackerSignal;
        }

        //TobiiXR.Start();
        // 获取VR头显的分辨率
        // Get the VR headset resolution from the external camera
        vrResolution = new Vector2(externalCamera.pixelWidth, externalCamera.pixelHeight);

        // Get the external display resolution from Screen settings
        externalResolution = new Vector2(Screen.width, Screen.height);

        // Calculate the central point in VR headset coordinates
        vrCenter = new Vector2(vrResolution.x / 2, vrResolution.y / 2);

        // Calculate the scaling factor based on the width
        scaleFactor = externalResolution.x / vrResolution.x;

        // Calculate the center of the external display
        externalCenter = externalResolution / 2;


        isHMDConnected = XRSettings.isDeviceActive;
        //trackedPoseDriver = GetComponent<TrackedPoseDriver>();
        Invoke("SystemCheck", 0.5f);                // System check.
        //SRanipal_Eye_v2.LaunchEyeCalibration();     // Perform calibration for eye tracking.
        Invoke("Measurement", 0.5f);                // Start the measurement of ocular movements in a separate callback function.  

        // ********************************************************************************************************************
        //
        //  Connection for LSL. Choose one from LSL and socket.
        //
        // ********************************************************************************************************************
        // Get command line arguments
        string[] args = System.Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            lslUUID = args[1];
            Debug.Log("Received LSL UUID: " + lslUUID);
        }
        else
        {
            lslUUID = defaultUUID;
            Debug.LogWarning("No LSL UUID provided. Using default UUID: " + defaultUUID);
        }
        streamInfo = new StreamInfo("UnityEyeData", "VR", channelCount, captureRate, channel_format_t.cf_float32, lslUUID);
        XMLElement chns = streamInfo.desc().append_child("channels");
        string[] channelNames = new string[] { "MeasureTime(100ns)", "TimeStamp(ms)", "Frame", "EyeValidL", "EyeValidR", "OpennessL", "OpennessR", "PupilDiameterL(mm)", "PupilDiameterR(mm)", "PosSensorL_X", "PosSensorL_Y", "PosSensorR_X",
            "PosSensorR_Y", "GazeOriginL_X", "GazeOriginL_Y", "GazeOriginL_Z", "GazeOriginR_X", "GazeOriginR_Y", "GazeOriginR_Z", "GazeDirectL_X", "GazeDirectL_Y", "GazeDirectL_Z", "GazeDirectR_X", "GazeDirectR_Y",
            "GazeDirectR_Z", "GazeSensitive", "FrownL", "FrownR", "SqueezeL", "SqueezeR", "WideL", "WideR", "DistanceValidC", "DistanceC(mm)", "TrackImpCnt", "Gaze_Left_X", "Gaze_Left_Y", "Gaze_Right_X", "Gaze_Right_Y",
            "Distance_left_eye", "Distance_right_eye",
            "ET_HeadRotationX","ET_HeadRotationY","ET_HeadRotationZ",
            "ET_HeadPositionVectorX","ET_HeadPositionVectorY","ET_HeadPositionVectorZ",
            "ET_HeadVelocityX","ET_HeadVelocityY","ET_HeadVelocityZ",
            "ET_HeadAngularVelocityX","ET_HeadAngularVelocityY","ET_HeadAngularVelocityZ",
            "ET_VR_HeadsetConnectedState","ET_VR_UserPresentState"
        };
        foreach (string name in channelNames)
        {
            chns.append_child("channel").append_child_value("label", name);
        }
        
        dataSendThread = new Thread(new ThreadStart(SendDataThread));
        dataSendThread.Start();

        LeftVisual = new GameObject("Left gaze ray visual");
        RightVisual = new GameObject("Right gaze ray visual");
        LineRenderer leftLineRenderer = LeftVisual.AddComponent<LineRenderer>();
        LineRenderer rightLineRenderer = RightVisual.AddComponent<LineRenderer>();
        InitLineRenderer(leftLineRenderer);
        InitLineRenderer(rightLineRenderer);
        LeftVisual.SetActive(false);
        RightVisual.SetActive(false);

        // Create spheres at the end of the lines
        LeftSphere = CreateSphereAtLineEnd(leftLineRenderer, Color.red);
        RightSphere = CreateSphereAtLineEnd(rightLineRenderer, Color.yellow);
    }

    // Update is called once per frame
    void Update()
    {
        RawGazeRays localGazeRays;
        GetGazeRays(out localGazeRays);
        RawGazeRays gazeRays = localGazeRays.Absolute(externalCamera.transform);
        //RawGazeRays gazeRays = localGazeRays.Absolute(this.transform);
        RenderGazeRays(gazeRays);
        // Assuming the line renderers are updated somewhere in your script
        UpdateSpherePosition(LeftSphere, LeftVisual.GetComponent<LineRenderer>());
        UpdateSpherePosition(RightSphere, RightVisual.GetComponent<LineRenderer>());

        // 获取节点状态列表
        InputTracking.GetNodeStates(nodeStates);
        foreach (XRNodeState nodeState in nodeStates)
        {
            if (nodeState.nodeType == XRNode.CenterEye)
            {
                nodeState.TryGetPosition(out eyePosition);
                nodeState.TryGetRotation(out eyeRotation);
                nodeState.TryGetPosition(out eyePosition);
                nodeState.TryGetRotation(out eyeRotation);
            }
            else if (nodeState.nodeType == XRNode.Head)
            {
                nodeState.TryGetRotation(out headRotation);
                nodeState.TryGetPosition(out headPosition);
                nodeState.TryGetVelocity(out headVelocity);
                nodeState.TryGetAngularVelocity(out headAngularVelocity);
            }
        }

        float staleSec = GetEyeStaleSeconds();
        if (staleSec > 2.0f)
        {
            if (!eyeStalled)
            {
                eyeStalled = true;
                Debug.LogWarning("UnityEyeData eye callback stalled for " + staleSec.ToString("F2") + "s. Attempting recovery.");
            }
            TryRecoverEye();
        }
        else if (eyeStalled)
        {
            eyeStalled = false;
            Debug.Log("UnityEyeData eye callback recovered after stall.");
        }
        /*
        var eyeTrackingData = TobiiXR.GetEyeTrackingData(TobiiXR_TrackingSpace.World);
       //Debug.Log($"eyeTrackingData {eyeTrackingData}");
        if (eyeTrackingData.IsLeftEyeBlinking || eyeTrackingData.IsRightEyeBlinking)
        {
            Debug.Log($"Blink Detected  {eyeTrackingData.IsLeftEyeBlinking}   {eyeTrackingData.IsRightEyeBlinking} eyeTrackingData.GazeRay.IsValid {eyeTrackingData.GazeRay.IsValid}");
        }
        else if (eyeTrackingData.GazeRay.IsValid)
        {
            Vector3 tobiigazePoint = eyeTrackingData.GazeRay.Origin + eyeTrackingData.GazeRay.Direction * 10.0f;
            Debug.Log("Gaze Point: " + tobiigazePoint);
        }
        */
        
        //headeulerAngles = headRotation.eulerAngles;
    }

    void SendDataThread()
    {
        outlet = new StreamOutlet(streamInfo);
        /*
        while (continueSending)
        {
            float[,] chunkData;
            lock (dataLock)
            {
                if (dataQueue.Count == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }
                chunkData = new float[dataQueue.Count, channelCount];
                for (int i = 0; i < dataQueue.Count; i++)
                {
                    for (int j = 0; j < channelCount; j++)
                    {
                        chunkData[i, j] = dataQueue[i][j];
                    }
                }
                dataQueue.Clear();
            }
            outlet.push_chunk(chunkData);
            //Debug.Log("Chunk data sent.");
        }
        */
    }

    // ********************************************************************************************************************
    //
    //  Check if the system works properly.
    //
    // ********************************************************************************************************************
    void SystemCheck()
    {
        if (SRanipal_Eye_API.GetEyeData_v2(ref eyeData) == ViveSR.Error.WORK)
        {
            Debug.Log("Device is working properly.");
        }

        if (SRanipal_Eye_API.GetEyeParameter(ref eye_parameter) == ViveSR.Error.WORK)
        {
            Debug.Log("Eye parameters are measured.");
        }

        //  Check again if the initialisation of eye tracking functions successfully. If not, we stop playing Unity.
        Error result_eye_init = SRanipal_API.Initial(SRanipal_Eye_v2.ANIPAL_TYPE_EYE_V2, IntPtr.Zero);

        if (result_eye_init == Error.WORK)
        {
            Debug.Log("[SRanipal] Initial Eye v2: " + result_eye_init);
        }
        else
        {
            Debug.LogError("[SRanipal] Initial Eye v2: " + result_eye_init);
        }
    }

    // ********************************************************************************************************************
    //
    //  Callback function to record the eye movement data.
    //  Note that SRanipal_Eye_v2 does not work in the function below. It only works under UnityEngine.
    //
    // ********************************************************************************************************************
    private static void EyeCallback(ref EyeData_v2 eye_data)
    {
        eyeData = eye_data;
        time_stamp = eyeData.timestamp;
        frame = eyeData.frame_sequence;
        // 使用一次性赋值，简化数据获取
        Vector2 pos_sensor_L = eyeData.verbose_data.left.pupil_position_in_sensor_area;
        Vector2 pos_sensor_R = eyeData.verbose_data.right.pupil_position_in_sensor_area;
        Vector3 gaze_origin_L = eyeData.verbose_data.left.gaze_origin_mm;
        Vector3 gaze_origin_R = eyeData.verbose_data.right.gaze_origin_mm;
        Vector3 gaze_direct_L = eyeData.verbose_data.left.gaze_direction_normalized;
        Vector3 gaze_direct_R = eyeData.verbose_data.right.gaze_direction_normalized;
        // 为 data 数组赋值
        data[0] = time_stamp;
        data[1] = time_stamp;
        data[2] = frame;
        data[3] = eyeData.verbose_data.left.eye_data_validata_bit_mask;
        data[4] = eyeData.verbose_data.right.eye_data_validata_bit_mask;
        data[5] = eyeData.verbose_data.left.eye_openness;
        data[6] = eyeData.verbose_data.right.eye_openness;
        data[7] = eyeData.verbose_data.left.pupil_diameter_mm;
        data[8] = eyeData.verbose_data.right.pupil_diameter_mm;
        data[9] = pos_sensor_L.x;
        data[10] = pos_sensor_L.y;
        data[11] = pos_sensor_R.x;
        data[12] = pos_sensor_R.y;
        data[13] = gaze_origin_L.x;
        data[14] = gaze_origin_L.y;
        data[15] = gaze_origin_L.z;
        data[16] = gaze_origin_R.x;
        data[17] = gaze_origin_R.y;
        data[18] = gaze_origin_R.z;
        data[19] = gaze_direct_L.x;
        data[20] = gaze_direct_L.y;
        data[21] = gaze_direct_L.z;
        data[22] = gaze_direct_R.x;
        data[23] = gaze_direct_R.y;
        data[24] = gaze_direct_R.z;
        data[25] = time_stamp;
        data[26] = eyeData.expression_data.left.eye_frown;
        data[27] = eyeData.expression_data.right.eye_frown;
        data[28] = eyeData.expression_data.left.eye_squeeze;
        data[29] = eyeData.expression_data.right.eye_squeeze;
        data[30] = eyeData.expression_data.left.eye_wide;
        data[31] = eyeData.expression_data.right.eye_wide;
        data[32] = eyeData.verbose_data.combined.convergence_distance_validity ? 1.0f : 0.0f;
        data[33] = eyeData.verbose_data.combined.convergence_distance_mm;
        data[34] = Convert.ToSingle(eyeData.verbose_data.tracking_improvements.count);

        // 处理 eye screen points
        data[35] = leftEyeScreenPoint.x;
        data[36] = leftEyeScreenPoint.y;
        data[37] = rightEyeScreenPoint.x;
        data[38] = rightEyeScreenPoint.y;

        // 将眼睛屏幕点添加到固定长度数组中
        leftEyeXArray[currentIndex] = leftEyeScreenPoint.x;
        leftEyeYArray[currentIndex] = leftEyeScreenPoint.y;
        rightEyeXArray[currentIndex] = rightEyeScreenPoint.x;
        rightEyeYArray[currentIndex] = rightEyeScreenPoint.y;

    // 循环更新索引
        currentIndex = (currentIndex + 1) % 5;

            // 计数器增加
        callbackCounter++;

        // 每10次运行一次平均值计算和写入共享内存的操作
        if (callbackCounter >= 5)
        {
            callbackCounter = 0; // 重置计数器

            float leftEyeXAvg = leftEyeXArray.Average();
            float leftEyeYAvg = leftEyeYArray.Average();
            float rightEyeXAvg = rightEyeXArray.Average();
            float rightEyeYAvg = rightEyeYArray.Average();
            //Debug.Log($"leftEyeXAvg: {leftEyeXAvg}, leftEyeYAvg: {leftEyeYAvg}");

            // 将平均值写入共享内存
            if (accessor_eye_tracker != null)
            {
                try
                {
                    accessor_eye_tracker.Write(0, leftEyeXAvg);
                    accessor_eye_tracker.Write(4, leftEyeYAvg);
                    accessor_eye_tracker.Write(8, rightEyeXAvg);
                    accessor_eye_tracker.Write(12, rightEyeYAvg);
                    loggedEyeTrackerMemoryWriteFailure = false;
                }
                catch (Exception ex)
                {
                    if (!loggedEyeTrackerMemoryWriteFailure)
                    {
                        Debug.LogWarning("Eye tracker shared memory write failed; continuing LSL eye data stream. " + ex.Message);
                        loggedEyeTrackerMemoryWriteFailure = true;
                    }
                }
            }
        }

        // 计算眼动追踪器与眼睛之间的距离  这里应该是错的，和imotion数据差别较大
        Vector3 leftEyePosition = eyeData.verbose_data.left.gaze_origin_mm;
        Vector3 rightEyePosition = eyeData.verbose_data.right.gaze_origin_mm;
        float distanceLeftEye = Vector3.Distance(headPosition, leftEyePosition);
        float distanceRightEye = Vector3.Distance(headPosition, rightEyePosition);
        data[39] = distanceLeftEye;
        data[40] = distanceRightEye;

        //ET_HeadRotationX,ET_HeadRotationY,ET_HeadRotationZ,
        data[41] = headRotation.x;
        data[42] = headRotation.y;
        data[43] = headRotation.z;
        //ET_HeadPositionVectorX,ET_HeadPositionVectorY,ET_HeadPositionVectorZ,
        data[44] = headPosition.x;
        data[45] = headPosition.y;
        data[46] = headPosition.z;
        //ET_HeadVelocityX,ET_HeadVelocityY,ET_HeadVelocityZ,
        data[47] = headVelocity.x;
        data[48] = headVelocity.y;
        data[49] = headVelocity.z;
        //ET_HeadAngularVelocityX,ET_HeadAngularVelocityY,ET_HeadAngularVelocityZ
        data[50] = headAngularVelocity.x;
        data[51] = headAngularVelocity.y;
        data[52] = headAngularVelocity.z;

        //ET_VR_HeadsetConnectedState,ET_VR_UserPresentState
        data[53] = isHMDConnected ? 1f : 0f;
        data[54] = 1;

        try
        {
            if (outlet != null)
            {
                outlet.push_sample(data);
                lastEyeSampleRealtime = Time.realtimeSinceStartup;
                loggedLslPushFailure = false;
            }
        }
        catch (Exception ex)
        {
            if (!loggedLslPushFailure)
            {
                Debug.LogWarning("UnityEyeData LSL push failed. " + ex.Message);
                loggedLslPushFailure = true;
            }
        }
    }

    public static void InitializeMemoryMappedFile(string sharedMemoryName)
    {
        // 计算所需的共享内存大小
        int frameSize = 4 * 4 ;  // 4个float 

        try
        {
            mmf_eye_tracker = MemoryMappedFile.OpenExisting(sharedMemoryName);
            mmf_eye_tracker.Dispose();  // 释放现有的 MemoryMappedFile 资源
            mmf_eye_tracker = MemoryMappedFile.CreateNew(sharedMemoryName, frameSize);
            Debug.Log("Found existing MemoryMappedFile.");
        }
        catch (FileNotFoundException)
        {
            Debug.Log("Did not find existing MemoryMappedFile.");
            mmf_eye_tracker = MemoryMappedFile.CreateNew(sharedMemoryName, frameSize);
        }

        accessor_eye_tracker = mmf_eye_tracker.CreateViewAccessor();


    }





    // ********************************************************************************************************************
    //
    //  Measure eye movements in a callback function that HTC SRanipal provides.
    //
    // ********************************************************************************************************************
    void Measurement()
    {
        SRanipal_Eye_API.GetEyeParameter(ref eye_parameter);

        if (SRanipal_Eye_Framework.Instance.EnableEyeDataCallback == true && eye_callback_registered == false)
        {
            SRanipal_Eye_v2.WrapperRegisterEyeDataCallback(eyeCallbackPtr);
            eye_callback_registered = true;
        }

        else if (SRanipal_Eye_Framework.Instance.EnableEyeDataCallback == false && eye_callback_registered == true)
        {
            SRanipal_Eye_v2.WrapperUnRegisterEyeDataCallback(eyeCallbackPtr);
            eye_callback_registered = false;
        }
    }

    public float GetEyeStaleSeconds()
    {
        if (lastEyeSampleRealtime < 0f)
        {
            return float.PositiveInfinity;
        }
        return Time.realtimeSinceStartup - lastEyeSampleRealtime;
    }

    public bool TryRecoverEye()
    {
        float now = Time.realtimeSinceStartup;
        if (now - lastRecoverAttemptRealtime < 5f)
        {
            return false;
        }
        lastRecoverAttemptRealtime = now;

        try
        {
            if (SRanipal_Eye_Framework.Instance != null && !SRanipal_Eye_Framework.Instance.EnableEyeDataCallback)
            {
                Debug.LogWarning("SRanipal callback recovery skipped because EnableEyeDataCallback is disabled.");
                return false;
            }

            if (eye_callback_registered)
            {
                SRanipal_Eye_v2.WrapperUnRegisterEyeDataCallback(eyeCallbackPtr);
            }

            SRanipal_Eye_v2.WrapperRegisterEyeDataCallback(eyeCallbackPtr);
            eye_callback_registered = true;
            loggedRecoverFailure = false;
            Debug.Log("Re-registered SRanipal eye callback after stall.");
            return true;
        }
        catch (Exception ex)
        {
            if (!loggedRecoverFailure)
            {
                Debug.LogWarning("Failed to recover SRanipal eye callback. " + ex.Message);
                loggedRecoverFailure = true;
            }
            return false;
        }
    }

    void InitLineRenderer(LineRenderer lr)
    {
        lr.startWidth = 0.005f;
        lr.endWidth = 0.005f;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        // 设置颜色为橙色
        Color orangeColor = new Color(1f, 0.5f, 0f, 1f); // RGBA 值为 (1, 0.5, 0) 对应橙色
        lr.startColor = orangeColor;
        lr.endColor = orangeColor;

    }

    public Vector3 GetSphereScreenPosition(Vector3 worldPosition)
    {
        if (externalCamera != null)  // 
        {
            Vector3 screenPosition = externalCamera.WorldToScreenPoint(worldPosition);
            // Convert the screen point from the main camera to the external screen's coordinates
            Vector2 pointOnScreen = ConvertToExternalScreenCoordinates(screenPosition);
            return pointOnScreen;  //
        }
        else
        {
            Debug.LogError("Main camera is not found.");
            return Vector3.zero;  //
        }
    }

    GameObject CreateSphereAtLineEnd(LineRenderer lr, Color sphereColor)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.transform.localScale = new Vector3(1f, 1f, 1f); // Adjust size as needed

        // Set the color of the sphere
        Renderer sphereRenderer = sphere.GetComponent<Renderer>();
        sphereRenderer.material = new Material(Shader.Find("Standard")); // Ensure there is a material to color
        sphereRenderer.material.color = sphereColor; // Apply the color

        sphere.SetActive(false);

        return sphere;
    }

    // Store the results from GetGazeRay for both eyes
    struct RawGazeRays
    {
        public Vector3 leftOrigin;
        public Vector3 leftDir;

        public Vector3 rightOrigin;
        public Vector3 rightDir;

        // Gaze origin and direction are in local coordinates relative to the
        // camera. Here we convert them to absolute coordinates.
        public RawGazeRays Absolute(Transform t)
        {
            var ans = new RawGazeRays();
            ans.leftOrigin = t.TransformPoint(leftOrigin);
            ans.rightOrigin = t.TransformPoint(rightOrigin);
            ans.leftDir = t.TransformDirection(leftDir);
            ans.rightDir = t.TransformDirection(rightDir);
            return ans;
        }
    }
    void GetGazeRays(out RawGazeRays r)
    {
        r = new RawGazeRays();
        if (eye_callback_registered)
        {
            // These return a bool depending whether the gaze ray is available.
            // We can ignore this return value for now.
            SRanipal_Eye_v2.GetGazeRay(GazeIndex.LEFT, out r.leftOrigin, out r.leftDir, eyeData);
            SRanipal_Eye_v2.GetGazeRay(GazeIndex.RIGHT, out r.rightOrigin, out r.rightDir, eyeData);
        }
        else
        {
            SRanipal_Eye_v2.GetGazeRay(GazeIndex.LEFT, out r.leftOrigin, out r.leftDir);
            SRanipal_Eye_v2.GetGazeRay(GazeIndex.RIGHT, out r.rightOrigin, out r.rightDir);
        }
    }

    // Call this method to get the 2D screen point from the 3D gaze point
    public Vector2 GetGazePointOnScreen2(Vector3 gazeOrigin, Vector3 gazeDirection)
    {
        // Project the gaze point forward in 3D space (you can adjust the distance as needed)
        Vector3 pointInWorld = gazeOrigin + gazeDirection * 200.0f; // Example distance: 10 units forward

        // Convert the 3D point to a 2D screen point
        Vector3 pointOnScreen = externalCamera.WorldToScreenPoint(pointInWorld);

        // Return the screen point as Vector2 (since Z is not needed)
        return new Vector2(pointOnScreen.x, pointOnScreen.y);
    }

    public Vector2 GetGazePointOnScreen(Vector3 gazeOrigin, Vector3 gazeDirection)
    {
        // Use the positions of the left and right eyes to calculate the gaze point
        // Vector3 gazeOrigin = (leftEyePosition + rightEyePosition) / 2.0f; // Use the midpoint between the eyes
        // Vector3 gazeDirection = (rightEyePosition - leftEyePosition).normalized; // Direction from left to right eye


        // Project the gaze point forward in 3D space (you can adjust the distance as needed)
        Vector3 pointInWorld = gazeOrigin + gazeDirection * 200.0f; // Example distance: 20 units forward

        // Convert the 3D point to a 2D screen point relative to the main camera
        Vector3 pointOnScreenRelativeToMainCamera = externalCamera.WorldToScreenPoint(pointInWorld);

        // Convert the screen point from the main camera to the external screen's coordinates
        //Vector2 pointOnScreen = ConvertToExternalScreenCoordinates(pointOnScreenRelativeToMainCamera);
        Vector2 pointOnScreen = ConvertToExternalDisplay(pointOnScreenRelativeToMainCamera);

        //Vector2 pointOnScreen = ConvertToRecordingResolution(pointOnScreenRelativeToMainCamera);

        // Return the screen point as Vector2 (since Z is not needed)
        return pointOnScreen;

        //return new Vector2(pointOnScreenRelativeToMainCamera.x, pointOnScreenRelativeToMainCamera.y);
    }



    private Vector2 ConvertToExternalScreenCoordinates(Vector3 pointOnScreenRelativeToMainCamera)
    {  
        // Calculate the ratio of the main camera's width and height to the external screen's width and height
        float widthRatio = (float)Screen.width / externalCamera.pixelWidth;
        float heightRatio = (float)Screen.height / externalCamera.pixelHeight;

        // Scale the screen point accordingly to match the external screen's dimensions
        float scaledX = pointOnScreenRelativeToMainCamera.x * widthRatio;
        float scaledY = Screen.height - pointOnScreenRelativeToMainCamera.y * heightRatio; // Invert y-axis

        // Return the scaled screen point as Vector2
        return new Vector2(scaledX, scaledY);
    }

    public Vector2 ConvertToExternalDisplay(Vector3 pointOnScreenRelativeToMainCamera)
    {
        float scaledX = pointOnScreenRelativeToMainCamera.x / 2;

        // 下面这样写R的ivt 可以运算，但是这样写不对
        //float scaledY = Screen.height - pointOnScreenRelativeToMainCamera.y / 2; // Invert y-axis

        // 下面的看起来是正确的
        float scaledY = externalCamera.pixelHeight / 2  - pointOnScreenRelativeToMainCamera.y / 2; // Invert y-axis

        // Return the scaled screen point as Vector2
        return new Vector2(scaledX, scaledY);
    }

    public Vector2 ConvertToRecordingResolution(Vector3 pointOnScreenRelativeToMainCamera)
    {
        float scaledX = pointOnScreenRelativeToMainCamera.x / 2;
        float scaledY = pointOnScreenRelativeToMainCamera.y / 2;

        // Return the scaled screen point as Vector2
        return new Vector2(scaledX, scaledY);
    }

    // Method to convert VR headset coordinates to external display coordinates
    public Vector2 ConvertToExternalDisplay2(Vector3 pointOnScreenRelativeToMainCamera)
    {
        // Extract the x and y coordinates
        float vrGazeX = pointOnScreenRelativeToMainCamera.x;
        float vrGazeY = pointOnScreenRelativeToMainCamera.y;

        // Create a Vector2 from the point coordinates
        Vector2 vrGazeCoordinates = new Vector2(vrGazeX, vrGazeY);

        // Calculate the difference from the VR center
        Vector2 offsetFromCenter = vrGazeCoordinates - vrCenter;

        // Apply the scaling factor to both x and y offsets
        Vector2 externalGazeCoordinates = offsetFromCenter * scaleFactor;

        // Translate to the center of the external display
        externalGazeCoordinates += externalCenter;

        return externalGazeCoordinates;
    }

    void RenderGazeRays(RawGazeRays gr)
    {
        //ClearPreviousCircles();

        LineRenderer llr = LeftVisual.GetComponent<LineRenderer>();
        llr.SetPosition(0, gr.leftOrigin);
        llr.SetPosition(1, gr.leftOrigin + gr.leftDir * 200);

        LineRenderer rlr = RightVisual.GetComponent<LineRenderer>();
        rlr.SetPosition(0, gr.rightOrigin);
        rlr.SetPosition(1, gr.rightOrigin + gr.rightDir * 200);

        ViveSR.Error error = SRanipal_Eye_API.GetEyeData_v2(ref eyeData);

        // Get the screen points for the left and right eyes
        leftEyeScreenPoint = GetGazePointOnScreen(gr.leftOrigin, gr.leftDir);
        rightEyeScreenPoint = GetGazePointOnScreen(gr.rightOrigin, gr.rightDir);

        

        // Get the screen coordinates for LeftSphere and RightSphere
        (Vector2 leftSphereScreenPos, Vector2 rightSphereScreenPos) = GetSphereScreenCoordinates();
        leftEyeScreenPoint = leftSphereScreenPos;
        rightEyeScreenPoint = rightSphereScreenPos;
        if (eyeDataText != null)
        {
            /*
            eyeDataText.text = $"Camera Resolution: {externalCamera.pixelWidth} x {externalCamera.pixelHeight}\n" +
                               $"Screen Resolution: {Screen.width} x {Screen.height}\n" +
                               $"leftEyeScreenPoint: {leftEyeScreenPoint}\n" +
                               $"rightEyeScreenPoint: {rightEyeScreenPoint}\n" +
                               $"leftSphere Screen Position: {leftSphereScreenPos}\n" +
                               $"rightSphere Screen Position: {rightSphereScreenPos}\n";
            */
        }
    }

    public (Vector2, Vector2) GetSphereScreenCoordinates()
    {
        if (externalCamera != null)
        {
            // Get the screen positions for LeftSphere and RightSphere
            Vector2 leftSphereScreenPos = ConvertToExternalDisplay(externalCamera.WorldToScreenPoint(LeftSphere.transform.position));
            Vector2 rightSphereScreenPos = ConvertToExternalDisplay(externalCamera.WorldToScreenPoint(RightSphere.transform.position));

            return (leftSphereScreenPos, rightSphereScreenPos);
        }
        else
        {
            Debug.LogError("External camera is not set.");
            return (Vector2.zero, Vector2.zero);
        }
    }

    void UpdateSpherePosition(GameObject sphere, LineRenderer lr)
    {
        if (lr.positionCount > 0)
        {
            sphere.transform.position = lr.GetPosition(lr.positionCount - 1);
        }
    }

    void OnApplicationQuit()
    {
        continueSending = false;
        if (dataSendThread != null && dataSendThread.IsAlive)
        {
            dataSendThread.Join();
        }
        if (writer != null)
        {
            writer.Close();
        }
        if (client != null)
        {
            client.Close();
        }
    }
    // Add a method to close the LSL StreamOutlet

}
// Store the results from GetGazeRay for both eyes







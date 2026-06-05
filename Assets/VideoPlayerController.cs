using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;
using System.IO;
using WebSocketSharp;
using LSL;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks; // 引入命名空间用于Task
using System.Collections;
public class ExternalVideoLoader : MonoBehaviour
{
    public VideoPlayer videoPlayer;
    public AudioSource audioSource;
    public List<string> stimuliFileNames = new List<string> { "a.mp4", "b.mp4" };
    private Dictionary<string, string> decryptedFiles = new Dictionary<string, string>();
    private int currentVideoIndex = 0;
    private WebSocket ws;
    private string steamVRSettingsPath;
    private string steamVRSettingsMetaPath;
    private bool playbackStarted = false; // Flag to control playback state
    // LSL variables
    private StreamInfo markerStreamInfo;
    private StreamOutlet markerOutlet;

    // For eye calibration communication
    private ConcurrentQueue<string> commandQueue = new ConcurrentQueue<string>();

    // New playback status variable
    private string playbackStatus = "natural_end";

    // New variables to store experiment and subject IDs and token
    private int currentExperimentID = -1;
    private int currentSubjectID = -1;
    private string experimentToken = "unknown_token";
    private string RecordingPath = "D:\\";
    private string lslUUID = "unknown_lsl_uuid";
    private string MemoryMappedFileshortUuid = "Local\\3759g7f";
    private const string DecryptingMarker = "__decrypting__";
    private const string ErrorMarkerPrefix = "__error__:";
    private readonly object decryptedFilesLock = new object();
    private readonly HashSet<string> decryptingFiles = new HashSet<string>();
    private string videosDirectory;
    private string tempVideoDirectory;
    private string activeVideoName = "";
    private string activeVideoPath = "";
    private bool waitingForPrepare = false;
    private Coroutine waitForVideoCoroutine;
    private int activeSegmentId = 0;
    private bool activeSegmentOpen = false;
    private string activeSegmentName = "";
    private string activeSegmentRecordingPath = "";
    private float activeSegmentStartRealtime = 0f;

    public Camera mainCamera; // ͷ���е��������

    public byte[] key = { 0x4a, 0x3b, 0x2c, 0x1d, 0xe2, 0xf3, 0xa4, 0xb5, 0x86, 0x97, 0x08, 0x19, 0xaa, 0xbb, 0xcc, 0xdd };
    private string tempVideoPath;

        // 定义需要解密的视频文件列表
    private List<string> filesToDecrypt =  new List<string>
    {
        "诱发_片头.mp4",
        "诱发_卧室独自吸毒.mp4",
        "诱发_会所吸毒.mp4",
        "诱发_网络赌博打鱼吸毒.mp4",
        "诱发_酒店助性吸毒.mp4",
        "诱发_打牌吸毒.mp4",
        "矫正_幻追债+幻警A+烂脸+被抓.mp4",
        "矫正_被抓B+家破+性猝死+衰老.mp4",
        "矫正_吵架+幻警C+皮肤病+跳楼.mp4",
        "矫正_幻失态+有虫+幻警B+掉牙B+直接猝死.mp4",
        "矫正_吃屎+烂腿+掉牙A+砍死.mp4",
        "矫正_崩溃+性病+水泡+奸杀.mp4",
        "静息海浪.mp4",
        "静息海浪静态.mp4",
        "回归.mp4"
    };

    void Start()
    {
        Application.runInBackground = true;
        videosDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../mdsk_external/Videos"));
        tempVideoDirectory = Path.Combine(Path.GetTempPath(), "MDSK_360_VideoPlayer");
        Directory.CreateDirectory(tempVideoDirectory);

        if (videoPlayer == null)
        {
            videoPlayer = GetComponent<VideoPlayer>();
        }

        videoPlayer.playOnAwake = false;
        videoPlayer.skipOnDrop = true;
        videoPlayer.prepareCompleted += OnPrepareCompleted;
        videoPlayer.errorReceived += OnVideoError;

        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        videoPlayer.loopPointReached += OnVideoEnd;

        // Get command line arguments
        string[] args = System.Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            lslUUID = args[1];
            SendSignal("Received LSL UUID: " + lslUUID);
            if (args.Length > 2)
            {
                MemoryMappedFileshortUuid = args[2];
            }
        }
        else
        {
            SendSignal("No LSL UUID provided. Using default UUID: " + lslUUID);
        }

        Guid uuid = Guid.NewGuid();
    
        // 截取 UUID 的前8个字符，或者根据需要截取更短的部分
        MemoryMappedFileshortUuid = "Local\\" + uuid.ToString().Substring(0, 8);
        //OnSettingMemoryMappedFileSignalSent?.Invoke(MemoryMappedFileshortUuid);
        //MemoryMappedFileshortUuid = "Local\\3r";
        Debug.Log($"MemoryMappedFileshortUuid {MemoryMappedFileshortUuid}");
        OnSettingMemoryMappedFileSignalSent?.Invoke(MemoryMappedFileshortUuid);
        string MemoryMappedFileshortUuid_eye_tracker = "Local\\" + uuid.ToString().Substring(0, 8) + "_eye_tracker";
        OnEyeTrackerSignalSent?.Invoke(MemoryMappedFileshortUuid_eye_tracker);
        // Initialize LSL marker stream
        markerStreamInfo = new StreamInfo("StimuliEvents", "Markers", 1, 0, channel_format_t.cf_string, lslUUID);
        // Setting the channel name to "Events"
        markerStreamInfo.desc().append_child("channels").append_child("channel").append_child_value("label", "Events");
        markerOutlet = new StreamOutlet(markerStreamInfo);

        steamVRSettingsPath = Path.Combine(Application.streamingAssetsPath, "SteamVR_Settings.asset");
        steamVRSettingsMetaPath = Path.Combine(Application.streamingAssetsPath, "SteamVR_Settings.asset.meta");

        ws = new WebSocket("ws://localhost:8080/");
        ws.OnOpen += (sender, e) =>
        {
            SendSignal("360 player WebSocket connected successfully.");
        };
        ws.OnError += (sender, e) =>
        {
            Debug.Log("WebSocket connection error: " + e.Message);
            if (e.Exception != null)
                SendSignal("360 player Exception details: " + e.Exception.ToString());
        };
        ws.OnClose += (sender, e) =>
        {
            SendSignal("360 player WebSocket connection closed: " + e.Reason);
        };
        ws.OnMessage += (sender, e) =>
        {
            SendSignal("360 player Received Command: " + e.Data);
            commandQueue.Enqueue(e.Data); // Enqueue the command
        };
        ws.Connect();
        
        //Debug.Log($"111111111111");
        //stimuliFileNames = new List<string> { "e2.mp4", "e1.mp4" };
        //Task.Run(() => DecryptFiles());
        //stimuliFileNames = new List<string> { "e2.mp4"};
        //playbackStarted = true;

        //LoadVideoFromExternalPath(stimuliFileNames[currentVideoIndex]);
        //Debug.Log($"2222222222");
        /**/
        SendMemoryMappedFileshortUuid();
    }

    void HandleCommand(string command)
    {
        //SendSignal("HandleCommand:"+command);
        // Split the command by '|' and handle accordingly
        string[] commandParts = command.Split('|');

        if (commandParts[0] == "Commence_stimulus_presentation")
        {
            SendvrHMDresolution();
            //SendMemoryMappedFileshortUuid();
            if (commandParts.Length >= 5)
            {
                int experimentID;
                int subjectID;
                string token;

                if (int.TryParse(commandParts[1], out experimentID) && int.TryParse(commandParts[2], out subjectID))
                {
                    currentExperimentID = experimentID;
                    currentSubjectID = subjectID;
                    token = commandParts[3];
                    experimentToken = token;
                    RecordingPath = commandParts[4];
                    
                    List<string> updatedStimuliList = new List<string>();
                    for (int i = 5; i < commandParts.Length; i++)
                    {
                        updatedStimuliList.Add(commandParts[i]);
                    }

                    stimuliFileNames = updatedStimuliList;
                    SendSignal($"RecievedCommence_stimulus_presentation|Updated video list: {string.Join(", ", stimuliFileNames)}");
                    currentVideoIndex = 0; // Reset current video index
                    ResetSegmentState();
                    playbackStarted = true;
                    //SendSignal($"In Commence_stimulus_presentation.stimuliFileNames {stimuliFileNames} ");
                                        // Asynchronously decrypt files without blocking other operations
                    //Task.Run(() => DecryptFiles());
                    LoadVideoFromExternalPath(stimuliFileNames[currentVideoIndex]);
                }
                else
                {
                    SendSignal("Invalid experiment or subject ID.");
                }
            }
            else
            {
                SendSignal("Invalid Commence_stimulus_presentation command format.");
            }
        }
        if (commandParts[0] == "startCalibration")
        {
            SendSignal(commandParts[0]);
            if (commandParts.Length >= 2)
            {                    
                List<string> updatedStimuliList = new List<string>();
                for (int i = 1; i < commandParts.Length; i++)
                {
                    updatedStimuliList.Add(commandParts[i]);
                }

                stimuliFileNames = updatedStimuliList;

                SendSignal($"RecievedstartCalibration|Updated video list: {string.Join(", ", stimuliFileNames)}");
                PrimeFirstVideoForPlayback();
                //StartCoroutine(DecryptFilesCoroutine());
                OnCalibrationSignalSent?.Invoke();
                SendSignal("CalibrationCompleted");

            }
            else
            {
                SendSignal("Invalid startCalibration command format.");
            }
        }
        else
        {
            HandleSingleCommand(command);
        }
    }

    public event Action OnCalibrationSignalSent;
    public event Action<string> OnRecordingSignalSent;
    public event Action OnStopRecordingSignalSent;
    public event Action<string> OnSettingMemoryMappedFileSignalSent;
    public event Action<string> OnEyeTrackerSignalSent;

    void HandleSingleCommand(string command)
    {
        switch (command)
        {
            case "play":
                if (!playbackStarted) playbackStarted = true;
                TogglePlayPause();
                break;
            case "next":
                playbackStatus = "command_next"; // Update playback status
                SendSignal($"Received 'next' command. Currently playing: {stimuliFileNames[currentVideoIndex]}");
                if (playbackStarted) PlayNextVideo();
                break;
            case "forceEndByUser":
                SendCurrentMeidaEnd();
                break;
            case "endExperiment":
                EndExperiment(); 
                break;
            case "exit":
                ExitPlayer();
                break;
            //case "startCalibration":
                //Task.Run(() => DecryptFiles());
                //OnCalibrationSignalSent?.Invoke();
                //SendSignal("CalibrationCompleted");
                //break;
            case "health_check":
                SendHealthCheckResponse();
                break;
            default:
                if (command.StartsWith("load:"))
                {
                    string[] splitCommand = command.Split(':');
                    if (splitCommand.Length > 1)
                    {
                        stimuliFileNames = new List<string>(splitCommand[1].Split(','));
                        SendSignal("Updated video list: " + string.Join(", ", stimuliFileNames));
                    }
                }
                break;
        }
    }

    void SendCurrentMeidaEnd()
    {
        string currentFileName = currentVideoIndex >= 0 && currentVideoIndex < stimuliFileNames.Count
            ? stimuliFileNames[currentVideoIndex]
            : activeVideoName;
        EndActiveSegment(currentFileName, playbackStatus);
        var tempFiles = Directory.Exists(tempVideoDirectory) ? Directory.GetFiles(tempVideoDirectory, "*.mp4") : new string[0];
        foreach (var file in tempFiles)
        {
            try
            {
                DeleteGeneratedTempVideo(file);
            }
            catch (Exception ex)
            {
                Debug.LogError("Error deleting file " + file + ": " + ex.Message);
            }
        }
    }

    void SendMemoryMappedFileshortUuid()
    {
        SendSignal($"MemoryMappedFileshortUuid|{MemoryMappedFileshortUuid}");        
    }

    void EndExperiment()
    {
        playbackStarted = false;
        if (activeSegmentOpen)
        {
            EndActiveSegment(activeVideoName, "experiment_end");
        }
        videoPlayer.Stop();
                // 删除临时目录中所有的 .mp4 文件
        var tempFiles = Directory.Exists(tempVideoDirectory) ? Directory.GetFiles(tempVideoDirectory, "*.mp4") : new string[0];
        foreach (var file in tempFiles)
        {
            try
            {
                DeleteGeneratedTempVideo(file);
            }
            catch (Exception ex)
            {
                Debug.LogError("Error deleting file " + file + ": " + ex.Message);
            }
        }
        ClearRenderTexture((RenderTexture)videoPlayer.targetTexture); // Clear the render texture
        SendSignal("Experiment has ended. Video playback stopped.");
        ResetSteamVRConfig();
        //Application.Quit();
    }

    void ClearRenderTexture(RenderTexture rt)
    {
        if (rt == null) return;
        RenderTexture currentActiveRT = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = currentActiveRT;
    }

    void PrimeFirstVideoForPlayback()
    {
        if (stimuliFileNames == null || stimuliFileNames.Count == 0)
        {
            return;
        }

        EnsureVideoPathReadyAsync(stimuliFileNames[0]);
    }

    void EnsureVideoPathReadyAsync(string fileName)
    {
        string sourcePath = GetSourceVideoPath(fileName);
        lock (decryptedFilesLock)
        {
            if (decryptedFiles.ContainsKey(fileName) || decryptingFiles.Contains(fileName))
            {
                return;
            }

            if (!File.Exists(sourcePath))
            {
                decryptedFiles[fileName] = ErrorMarkerPrefix + "Video file not found: " + sourcePath;
                return;
            }

            if (!ShouldDecryptFile(sourcePath))
            {
                decryptedFiles[fileName] = sourcePath;
                return;
            }

            decryptedFiles[fileName] = DecryptingMarker;
            decryptingFiles.Add(fileName);
        }

        Task.Run(() => PrepareDecryptedVideo(fileName, sourcePath));
    }

    string GetSourceVideoPath(string fileName)
    {
        string baseDirectory = string.IsNullOrEmpty(videosDirectory)
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "../mdsk_external/Videos"))
            : videosDirectory;
        return Path.Combine(baseDirectory, fileName);
    }

    bool TryGetReadyVideoPath(string fileName, out string readyPath, out string failure)
    {
        readyPath = null;
        failure = null;

        lock (decryptedFilesLock)
        {
            if (!decryptedFiles.TryGetValue(fileName, out string path) || path == DecryptingMarker)
            {
                return false;
            }

            if (path.StartsWith(ErrorMarkerPrefix, StringComparison.Ordinal))
            {
                failure = path.Substring(ErrorMarkerPrefix.Length);
                return false;
            }

            if (!File.Exists(path))
            {
                failure = "Prepared video file missing: " + path;
                decryptedFiles.Remove(fileName);
                return false;
            }

            readyPath = path;
            return true;
        }
    }

    void PrepareDecryptedVideo(string fileName, string sourcePath)
    {
        string preparedPath = null;
        string error = null;

        try
        {
            preparedPath = GetPreparedVideoPath(fileName, sourcePath);
            if (!File.Exists(preparedPath) || !IsPlainMp4(preparedPath))
            {
                XorCopyFile(sourcePath, preparedPath);
            }

            if (!IsPlainMp4(preparedPath))
            {
                error = "Decrypted video header is not a valid MP4: " + fileName;
            }
        }
        catch (Exception ex)
        {
            error = "Video preparation failed for " + fileName + ": " + ex.Message;
        }

        lock (decryptedFilesLock)
        {
            decryptingFiles.Remove(fileName);
            decryptedFiles[fileName] = string.IsNullOrEmpty(error) ? preparedPath : ErrorMarkerPrefix + error;
        }
    }

    string GetPreparedVideoPath(string fileName, string sourcePath)
    {
        string directory = string.IsNullOrEmpty(tempVideoDirectory)
            ? Path.Combine(Path.GetTempPath(), "MDSK_360_VideoPlayer")
            : tempVideoDirectory;
        Directory.CreateDirectory(directory);

        FileInfo info = new FileInfo(sourcePath);
        string safeName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
        string suffix = info.Length.ToString("x") + "_" + info.LastWriteTimeUtc.Ticks.ToString("x");
        return Path.Combine(directory, safeName + "_" + suffix + ".mp4");
    }

    bool ShouldDecryptFile(string path)
    {
        byte[] header = ReadHeader(path);
        if (HeaderContainsFtyp(header))
        {
            return false;
        }

        byte[] decodedHeader = XorBytes(header);
        if (HeaderContainsFtyp(decodedHeader))
        {
            return true;
        }

        return filesToDecrypt.Contains(Path.GetFileName(path));
    }

    bool IsPlainMp4(string path)
    {
        return HeaderContainsFtyp(ReadHeader(path));
    }

    byte[] ReadHeader(string path)
    {
        byte[] header = new byte[32];
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = stream.Read(header, 0, header.Length);
            if (read == header.Length)
            {
                return header;
            }

            byte[] actual = new byte[read];
            Buffer.BlockCopy(header, 0, actual, 0, read);
            return actual;
        }
    }

    bool HeaderContainsFtyp(byte[] header)
    {
        for (int i = 0; i <= header.Length - 4; i++)
        {
            if (header[i] == (byte)'f' && header[i + 1] == (byte)'t' && header[i + 2] == (byte)'y' && header[i + 3] == (byte)'p')
            {
                return true;
            }
        }

        return false;
    }

    byte[] XorBytes(byte[] data)
    {
        byte[] output = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            output[i] = (byte)(data[i] ^ key[i % key.Length]);
        }
        return output;
    }

    void XorCopyFile(string sourcePath, string destinationPath)
    {
        string partialPath = destinationPath + ".part";
        byte[] buffer = new byte[4 * 1024 * 1024];

        using (FileStream sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream destStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            int bytesRead;
            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    buffer[i] ^= key[i % key.Length];
                }
                destStream.Write(buffer, 0, bytesRead);
            }
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }
        File.Move(partialPath, destinationPath);
    }

    bool IsGeneratedTempVideo(string path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(tempVideoDirectory))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        string fullTempDir = Path.GetFullPath(tempVideoDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullTempDir, StringComparison.OrdinalIgnoreCase);
    }

    void DeleteGeneratedTempVideo(string path)
    {
        if (IsGeneratedTempVideo(path) && File.Exists(path))
        {
            File.Delete(path);
            Debug.Log("Deleted decrypted file: " + path);
        }
    }
    IEnumerator DecryptFilesCoroutine()
    {
        DecryptFiles(); // 假设 DecryptFiles 是同步方法
        yield return null; // 等待一个帧确保它开始执行
    }
    void DecryptFiles2()
    {
        // 初始化或清空之前的解密文件路径记录
        decryptedFiles.Clear();

        // 删除临时目录中所有的 .mp4 文件
        var tempFiles = Directory.GetFiles(Path.GetTempPath(), "*.mp4");
        foreach (var file in tempFiles)
        {
            try
            {
                File.Delete(file);
                Debug.Log("Deleted old temp file: " + file);
            }
            catch (Exception ex)
            {
                Debug.LogError("Error deleting file " + file + ": " + ex.Message);
            }
        }

        // 首先为每个文件设置默认状态
        foreach (var fileName in stimuliFileNames)
        {
            string initialTempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            initialTempPath = Path.ChangeExtension(initialTempPath, ".mp4");
            decryptedFiles[fileName] = initialTempPath + "_decrypting"; // 加后缀表明解密未完成
        }

        // 开始解密过程
        foreach (var fileName in stimuliFileNames)
        {
            //string sourcePath = Path.Combine(Application.dataPath, fileName);
            string sourcePath = Path.Combine(Application.dataPath, "../mdsk_external/Videos", fileName);
            if (!File.Exists(sourcePath))
            {
                Debug.LogError("File not found: " + sourcePath);
                decryptedFiles[fileName] = "File not found"; // 更新状态为文件未找到
                continue;
            }

            string tempVideoPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            tempVideoPath = Path.ChangeExtension(tempVideoPath, ".mp4");
            using (FileStream sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read))
            using (FileStream destStream = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1024 * 1024]; // 使用1MB的缓冲区
                int bytesRead;
                while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        buffer[i] ^= key[i % key.Length]; // 应用XOR解密
                    }
                    destStream.Write(buffer, 0, bytesRead);
                }
            }
            Debug.Log(fileName + "  ->decryptedFiles[fileName]: " + tempVideoPath);
            decryptedFiles[fileName] = tempVideoPath; // 更新为解密后的实际文件路径
        }
    }

    void DecryptFiles()
    {
        // 初始化或清空之前的解密文件路径记录
        decryptedFiles.Clear();

        // 删除临时目录中所有的 .mp4 文件
        var tempFiles = Directory.GetFiles(Path.GetTempPath(), "*.mp4");
        foreach (var file in tempFiles)
        {
            try
            {
                File.Delete(file);
                Debug.Log("Deleted old temp file: " + file);
            }
            catch (Exception ex)
            {
                Debug.LogError("Error deleting file " + file + ": " + ex.Message);
            }
        }

        // 首先为每个文件设置默认状态
        foreach (var fileName in stimuliFileNames)
        {
            string initialTempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            initialTempPath = Path.ChangeExtension(initialTempPath, ".mp4");
            decryptedFiles[fileName] = initialTempPath + "_decrypting"; // 加后缀表明解密未完成
        }

        // 开始解密过程
        foreach (var fileName in stimuliFileNames)
        {
            string sourcePath = Path.Combine(Application.dataPath, "../mdsk_external/Videos", fileName);
            if (!File.Exists(sourcePath))
            {
                Debug.LogError("File not found: " + sourcePath);
                decryptedFiles[fileName] = "File not found"; // 更新状态为文件未找到
                continue;
            }

            if (!filesToDecrypt.Contains(fileName))
            {
                Debug.Log("Skipping decryption for: " + fileName);
                decryptedFiles[fileName] = sourcePath; // 设置为原始文件路径，跳过解密
                continue;
            }

            string tempVideoPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            tempVideoPath = Path.ChangeExtension(tempVideoPath, ".mp4");
            using (FileStream sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read))
            using (FileStream destStream = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1024 * 1024]; // 使用1MB的缓冲区
                int bytesRead;
                while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        buffer[i] ^= key[i % key.Length]; // 应用XOR解密
                    }
                    destStream.Write(buffer, 0, bytesRead);
                }
            }
            Debug.Log(fileName + "  ->decryptedFiles[fileName]: " + tempVideoPath);
            decryptedFiles[fileName] = tempVideoPath; // 更新为解密后的实际文件路径
        }
    }


    void LoadVideoFromExternalPath2(string fileName)
    {
        if (!playbackStarted) return;

        string path = Path.Combine(Application.dataPath, "../mdsk_external/Videos", fileName);
        if (!File.Exists(path))
        {
            Debug.Log("Video file not found: " + path);
            return;
        }

        string tempVideoPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        tempVideoPath = Path.ChangeExtension(tempVideoPath, ".mp4");

        try
        {
            using (FileStream sourceStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (FileStream destStream = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1024 * 1024]; // 1 MB buffer
                int bytesRead;
                while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        buffer[i] ^= key[i % key.Length];
                    }
                    destStream.Write(buffer, 0, bytesRead);
                }
            }

            videoPlayer.source = VideoSource.Url;
            videoPlayer.url = tempVideoPath;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            videoPlayer.SetTargetAudioSource(0, audioSource);
            videoPlayer.aspectRatio = VideoAspectRatio.Stretch;

            videoPlayer.prepareCompleted += OnPrepareCompleted;
            videoPlayer.Prepare();
        }
        catch (Exception ex)
        {
            Debug.LogError("Error processing video file: " + ex.Message);
        }
    }

    void LoadVideoFromExternalPath3(string fileName)
    {
        if (!playbackStarted) return;

        string path = Path.Combine(Application.dataPath, "../mdsk_external/Videos", fileName);
        if (!File.Exists(path))
        {
            Debug.Log("Video file not found: " + path);
            return;
        }

        byte[] encryptedData = File.ReadAllBytes(path);
        byte[] decryptedData = XOREncryptDecrypt(encryptedData, key);

        // 生成临时文件路径，但更改扩展名为.mp4
        tempVideoPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        tempVideoPath = Path.ChangeExtension(tempVideoPath, ".mp4");  // 更改扩展名为 .mp4
        File.WriteAllBytes(tempVideoPath, decryptedData);

        videoPlayer.source = VideoSource.Url;
        videoPlayer.url = tempVideoPath;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
        videoPlayer.SetTargetAudioSource(0, audioSource);
        videoPlayer.aspectRatio = VideoAspectRatio.Stretch;

        videoPlayer.prepareCompleted += OnPrepareCompleted;
        videoPlayer.Prepare();
    }


    void OnPrepareCompleted(VideoPlayer source)
    {
        if (source != videoPlayer || !waitingForPrepare)
        {
            return;
        }

        waitingForPrepare = false;
        Debug.Log("Preparation complete - video is ready to play: " + activeVideoName);
        SendMarker($"Media_Start|{activeVideoName}");
        SendSignal($"Media_Start|{activeVideoName}");

        string outputPath = Path.Combine(RecordingPath, Path.GetFileNameWithoutExtension(activeVideoName));
        BeginActiveSegment(activeVideoName, outputPath);
        OnRecordingSignalSent?.Invoke(outputPath);
        Debug.Log("OnRecordingSignalSent: " + outputPath);

        source.Play();
    }

    void OnVideoError(VideoPlayer source, string message)
    {
        waitingForPrepare = false;
        SendSignal($"VideoPlayerError|{activeVideoName}|{message}");
        Debug.LogError($"VideoPlayerError|{activeVideoName}|{message}");
    }

    void ResetSegmentState()
    {
        activeSegmentOpen = false;
        activeSegmentName = "";
        activeSegmentRecordingPath = "";
        activeSegmentStartRealtime = 0f;
    }

    void BeginActiveSegment(string fileName, string recordingPath)
    {
        if (activeSegmentOpen)
        {
            EndActiveSegment(activeSegmentName, "interrupted_by_new_segment");
        }

        activeSegmentId++;
        activeSegmentOpen = true;
        activeSegmentName = fileName;
        activeSegmentRecordingPath = recordingPath;
        activeSegmentStartRealtime = Time.realtimeSinceStartup;

        string detail = $"Media_Segment_Start|{activeSegmentId}|{fileName}|VideoIndex:{currentVideoIndex}|RecordingPath:{recordingPath}|VideoLength:{FormatSeconds(videoPlayer.length)}";
        SendSignal(detail);
        AppendSegmentAudit("START", fileName, "playing", 0f);
    }

    bool EndActiveSegment(string fileName, string status)
    {
        if (!activeSegmentOpen)
        {
            Debug.LogWarning("Ignoring duplicate media end for " + fileName + " because no active segment is open.");
            return false;
        }

        string endedSegmentName = string.IsNullOrEmpty(activeSegmentName) ? fileName : activeSegmentName;
        float elapsedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - activeSegmentStartRealtime);

        SendMarker($"Media_End|{endedSegmentName}");
        SendSignal($"Media_End|{endedSegmentName}|{status}");
        SendSignal($"Media_Segment_End|{activeSegmentId}|{endedSegmentName}|{status}|ElapsedSeconds:{FormatSeconds(elapsedSeconds)}|VideoTime:{FormatSeconds(videoPlayer.time)}|VideoLength:{FormatSeconds(videoPlayer.length)}");
        AppendSegmentAudit("END", endedSegmentName, status, elapsedSeconds);

        activeSegmentOpen = false;
        activeSegmentName = "";
        activeSegmentRecordingPath = "";
        OnStopRecordingSignalSent?.Invoke();
        return true;
    }

    string GetSegmentAuditPath()
    {
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../mdsk_external/EyeTrackRecords"));
        Directory.CreateDirectory(directory);
        string safeUuid = SanitizeFileName(string.IsNullOrEmpty(lslUUID) ? "unknown_lsl_uuid" : lslUUID);
        return Path.Combine(directory, "UnitySegmentAudit_" + safeUuid + ".tsv");
    }

    void AppendSegmentAudit(string eventName, string fileName, string status, float elapsedSeconds)
    {
        try
        {
            string path = GetSegmentAuditPath();
            bool writeHeader = !File.Exists(path);
            using (StreamWriter audit = new StreamWriter(path, true))
            {
                if (writeHeader)
                {
                    audit.WriteLine("event\tutc_time\tsegment_id\texperiment_id\tsubject_id\tlsl_uuid\tvideo_index\tfile_name\tstatus\trecording_path\tvideo_path\tvideo_time_seconds\tvideo_length_seconds\telapsed_seconds");
                }

                audit.WriteLine(string.Join("\t", new string[]
                {
                    Tsv(eventName),
                    Tsv(DateTime.UtcNow.ToString("o")),
                    activeSegmentId.ToString(),
                    currentExperimentID.ToString(),
                    currentSubjectID.ToString(),
                    Tsv(lslUUID),
                    currentVideoIndex.ToString(),
                    Tsv(fileName),
                    Tsv(status),
                    Tsv(activeSegmentRecordingPath),
                    Tsv(activeVideoPath),
                    FormatSeconds(videoPlayer.time),
                    FormatSeconds(videoPlayer.length),
                    FormatSeconds(elapsedSeconds)
                }));
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Failed to write Unity segment audit. " + ex.Message);
        }
    }

    string Tsv(string value)
    {
        if (value == null)
        {
            return "";
        }

        return value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
    }

    string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }

    string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "0.000";
        }

        return seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    }

    byte[] XOREncryptDecrypt(byte[] data, byte[] key)
    {
        byte[] output = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            output[i] = (byte)(data[i] ^ key[i % key.Length]);
        }
        return output;
    }

    void LoadVideoFromExternalPath_ori(string fileName)
    {
        if (!playbackStarted) return;
        
        string path = Path.Combine(Application.dataPath, "../mdsk_external/Videos", fileName);
        if (!File.Exists(path))
        {
            SendSignal("Video file not found: " + path);
            return;
        }
        videoPlayer.source = VideoSource.Url;
        videoPlayer.url = path;
        SendSignal("videoPlayer.url: " + path);
        videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
        videoPlayer.SetTargetAudioSource(0, audioSource);

        videoPlayer.aspectRatio = VideoAspectRatio.Stretch;
        
        videoPlayer.Prepare();

        SendMarker($"Media_Start|{fileName}");
        SendSignal($"Media_Start|{fileName}");
        
        if (fileName.EndsWith(".mp4"))
        {
            fileName = fileName.Substring(0, fileName.Length - 4);
        }
        string outputPath = Path.Combine(RecordingPath, fileName);

        OnRecordingSignalSent?.Invoke(outputPath);
        Debug.Log("OnRecordingSignalSent: " + outputPath);
        videoPlayer.Play();
    }
    void LoadVideoFromExternalPath(string fileName)
    {
        if (!playbackStarted) return;

        EnsureVideoPathReadyAsync(fileName);

        string readyPath;
        string failure;
        if (!TryGetReadyVideoPath(fileName, out readyPath, out failure))
        {
            if (!string.IsNullOrEmpty(failure))
            {
                SendSignal(failure);
                Debug.LogError(failure);
                return;
            }

            Debug.Log("Waiting for video file to be ready: " + fileName);
            if (waitForVideoCoroutine != null)
            {
                StopCoroutine(waitForVideoCoroutine);
            }
            waitForVideoCoroutine = StartCoroutine(WaitForDecryption(fileName));
            return;
        }

        StartPreparedPlayback(fileName, readyPath);
    }

    void StartPreparedPlayback(string fileName, string videoPath)
    {
        if (!File.Exists(videoPath))
        {
            SendSignal("Video file not found: " + videoPath);
            return;
        }

        if (videoPlayer.isPlaying || videoPlayer.isPrepared)
        {
            if (activeSegmentOpen)
            {
                EndActiveSegment(activeSegmentName, "interrupted_by_prepare");
            }

            videoPlayer.Stop();
        }

        activeVideoName = fileName;
        activeVideoPath = videoPath;
        waitingForPrepare = true;

        videoPlayer.source = VideoSource.Url;
        videoPlayer.url = videoPath;
        SendSignal("Video_Preparing|" + fileName + "|" + videoPath);
        videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
        videoPlayer.SetTargetAudioSource(0, audioSource);
        videoPlayer.aspectRatio = VideoAspectRatio.Stretch;

        try
        {
            videoPlayer.Prepare();
        }
        catch (Exception ex)
        {
            waitingForPrepare = false;
            SendSignal("Video prepare failed: " + ex.Message);
            Debug.LogError("Video prepare failed: " + ex);
        }
    }

    IEnumerator WaitForDecryption(string fileName)
    {
        string readyPath;
        string failure;

        while (!TryGetReadyVideoPath(fileName, out readyPath, out failure))
        {
            if (!string.IsNullOrEmpty(failure))
            {
                SendSignal(failure);
                Debug.LogError(failure);
                waitForVideoCoroutine = null;
                yield break;
            }

            yield return new WaitForSeconds(0.25f);
        }

        waitForVideoCoroutine = null;
        StartPreparedPlayback(fileName, readyPath);
    }

    void LoadVideoFromExternalPath_legacy(string fileName)
    {
        if (!playbackStarted) return;

        // 检查文件是否已解密且路径已更新
        if (!decryptedFiles.ContainsKey(fileName) || decryptedFiles[fileName].EndsWith("_decrypting"))
        {
            Debug.Log("Waiting for decryption to complete for file: " + fileName);
            StartCoroutine(WaitForDecryption(fileName));
            return;
        }

        string decryptedPath = decryptedFiles[fileName];
        if (!File.Exists(decryptedPath))
        {
            SendSignal("Decrypted video file not found: " + decryptedPath);
            return;
        }
        
        videoPlayer.source = VideoSource.Url;
        videoPlayer.url = decryptedPath; // 使用解密后的文件路径
        SendSignal("videoPlayer.url: " + decryptedPath);
        videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
        videoPlayer.SetTargetAudioSource(0, audioSource);

        videoPlayer.aspectRatio = VideoAspectRatio.Stretch;

        SendMarker($"Media_Start|{fileName}");
        SendSignal($"Media_Start|{fileName}");

        videoPlayer.Prepare(); // 准备播放解密后的视频

        string outputPath = Path.Combine(RecordingPath, Path.GetFileNameWithoutExtension(fileName));
        OnRecordingSignalSent?.Invoke(outputPath);
        Debug.Log("OnRecordingSignalSent: " + outputPath);
        videoPlayer.Play();
    }

    IEnumerator WaitForDecryption_legacy(string fileName)
    {
        // 等待解密完成
        while (!decryptedFiles.ContainsKey(fileName) || decryptedFiles[fileName].EndsWith("_decrypting"))
        {
            yield return new WaitForSeconds(1); // 每秒检查一次
        }
        // 解密完成后再次尝试加载视频
        LoadVideoFromExternalPath(fileName);
    }

    void SendHealthCheckResponse()
    {
        string statusMessage = "System operational. Current video index|" + currentVideoIndex;
        SendSignal(statusMessage);
    }

    void SendvrHMDresolution()
    {
        string statusMessage = $"VRheadsetScreenResolution|{mainCamera.pixelWidth / 2}|{mainCamera.pixelHeight / 2}";
        SendSignal(statusMessage);
    }

    void TogglePlayPause()
    {
        playbackStarted = true;
        if (videoPlayer.isPaused || !videoPlayer.isPlaying)
        {
            videoPlayer.Play();
            /*
            SendSignal($"1 Starting video playback. currentVideoIndex:{currentVideoIndex} stimuliFileNames[currentVideoIndex]{stimuliFileNames[currentVideoIndex]}");

            LoadVideoFromExternalPath(stimuliFileNames[currentVideoIndex]);
            //videoPlayer.Play();
            SendSignal($"2 Starting video playback. currentVideoIndex:{currentVideoIndex} stimuliFileNames[currentVideoIndex]{stimuliFileNames[currentVideoIndex]}");

            
            string fileName = stimuliFileNames[currentVideoIndex];

            // ����ļ����Ƿ������׺
            if (fileName.EndsWith(".mp4"))
            {
                // ȥ����׺
                fileName = fileName.Substring(0, fileName.Length - 4);
            }

            //string outputPath = $"D:\\{fileName}"; // Or any path you want to use
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outputPath = $"D:\\{fileName}_{timestamp}";
            OnRecordingSignalSent?.Invoke(outputPath);
            */
        }
        else
        {
            videoPlayer.Pause();
            SendSignal("Pausing video playback.");
            /*
            OnStopRecordingSignalSent?.Invoke();
            */
        }
    }
    void PlayNextVideo()
    {
        if (currentVideoIndex < 0 || currentVideoIndex >= stimuliFileNames.Count)
        {
            return;
        }

        string currentFileName = stimuliFileNames[currentVideoIndex];
        bool endedSegment = EndActiveSegment(currentFileName, playbackStatus);
        if (!endedSegment)
        {
            return;
        }

        playbackStatus = "natural_end"; // Reset playback status after sending the marker
        videoPlayer.Stop();

        // 删除刚播放完的视频文件并从字典中移除
        string filePath = null;
        lock (decryptedFilesLock)
        {
            if (decryptedFiles.TryGetValue(currentFileName, out filePath))
            {
                decryptedFiles.Remove(currentFileName);
            }
        }
        DeleteGeneratedTempVideo(filePath);

        currentVideoIndex++;

        if (currentVideoIndex < stimuliFileNames.Count)
        {
            LoadVideoFromExternalPath(stimuliFileNames[currentVideoIndex]);
        }
        else
        {
            SendSignal("Stimulus_presentation_end");
            videoPlayer.Stop();
        }
    }

    void PlayNextVideo_ori()
    {
        PlayNextVideo();
    }

    void ExitPlayer()
    {
        if (activeSegmentOpen)
        {
            EndActiveSegment(activeVideoName, "exit");
        }
        videoPlayer.Stop();
        //ResetSteamVRConfig();
        Application.Quit();
    }

    void ResetSteamVRConfig()
    {
        if (File.Exists(steamVRSettingsPath))
        {
            File.Delete(steamVRSettingsPath);
            File.Delete(steamVRSettingsMetaPath);
            SendSignal("SteamVR settings reset. Please restart the application.");
        }
    }
    void Cleanup()
    {
        List<string> filesToDelete = new List<string>();
        lock (decryptedFilesLock)
        {
            foreach (var entry in decryptedFiles)
            {
                filesToDelete.Add(entry.Value);
            }
            decryptedFiles.Clear();
            decryptingFiles.Clear();
        }

        foreach (var file in filesToDelete)
        {
            DeleteGeneratedTempVideo(file);
        }
    }
    void OnDestroy()
    {
        if (ws != null)
        {
            ws.Close();
            ws = null;
        }
        Cleanup();
    }

    void SendSignal(string message)
    {
        if (ws != null && ws.IsAlive)
        {
            ws.Send(FormatMessage(message));
        }
    }

    void SendMarker(string message)
    {
        if (markerOutlet != null)
        {
            // Directly push the message without formatting
            markerOutlet.push_sample(new string[] { message });
        }
    }

    string FormatMessage(string message)
    {
        return $"{message}|ExperimentID:{currentExperimentID}|SubjectID:{currentSubjectID}|Token:{experimentToken}|LSL_UUID:{lslUUID}|RecordingPath:{RecordingPath}";
    }

    private void OnVideoEnd(VideoPlayer vp)
    {
        PlayNextVideo();
    }

    void Update()
    {
        // Process commands from the queue
        while (commandQueue.TryDequeue(out string command))
        {
            HandleCommand(command);
        }

        // Check for key inputs
        if (Input.GetKeyDown(KeyCode.Q)) { ExitPlayer(); }
        if (Input.GetKeyDown(KeyCode.P)) { TogglePlayPause(); }
        if (Input.GetKeyDown(KeyCode.N)) { playbackStatus = "command_next"; PlayNextVideo(); }
        //if (Input.GetKeyDown(KeyCode.S)) { CaptureFrame(); }
    }

    void CaptureFrame()
    {
        string folderPath = Path.Combine(Application.dataPath, "../mdsk_external/CapturedFrames");
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        string fileName = $"{System.DateTime.Now:yyyyMMddHHmmss}.png";
        string filePath = Path.Combine(folderPath, fileName);
        ScreenCapture.CaptureScreenshot(filePath);
        SendSignal($"Frame captured: {filePath}");
    }
}

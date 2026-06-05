using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

public class VideoPlaybackDiagnostics : MonoBehaviour
{
    private static readonly byte[] Key = { 0x4a, 0x3b, 0x2c, 0x1d, 0xe2, 0xf3, 0xa4, 0xb5, 0x86, 0x97, 0x08, 0x19, 0xaa, 0xbb, 0xcc, 0xdd };
    private VideoPlayer videoPlayer;
    private float startedAt;
    private float lastProgressAt;
    private double lastVideoTime;
    private long lastFrame;
    private float lastSampleAt;
    private float lastFrameAdvanceAt;
    private float maxFrameHoldSeconds;
    private float sampleIntervalSeconds = 0.25f;
    private float stutterThresholdSeconds = 1.25f;
    private float logIntervalSeconds = 5f;
    private float lastLogAt;
    private bool prepared;
    private bool failed;
    private bool deletePreparedOnExit = true;
    private string sourcePath;
    private string preparedPath;
    private float durationSeconds = 45f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!HasArg(args, "--video-diagnostic") && !HasArg(args, "--videoDiagnostics"))
        {
            return;
        }

        foreach (ExternalVideoLoader loader in FindObjectsOfType<ExternalVideoLoader>())
        {
            loader.enabled = false;
        }
        foreach (GazeRays gaze in FindObjectsOfType<GazeRays>())
        {
            gaze.enabled = false;
        }
        foreach (CameraSync sync in FindObjectsOfType<CameraSync>())
        {
            sync.enabled = false;
        }

        GameObject go = new GameObject("VideoPlaybackDiagnostics");
        DontDestroyOnLoad(go);
        go.AddComponent<VideoPlaybackDiagnostics>();
    }

    private void Start()
    {
        Application.runInBackground = true;

        string[] args = Environment.GetCommandLineArgs();
        string videoArg = GetArgValue(args, "--videoFile", "诱发_片头.mp4");
        float.TryParse(GetArgValue(args, "--videoDuration", "45"), out durationSeconds);
        float.TryParse(GetArgValue(args, "--sampleInterval", "0.25"), out sampleIntervalSeconds);
        float.TryParse(GetArgValue(args, "--stutterSeconds", "1.25"), out stutterThresholdSeconds);
        float.TryParse(GetArgValue(args, "--logInterval", "5"), out logIntervalSeconds);
        deletePreparedOnExit = !HasArg(args, "--keepPreparedVideo");

        sourcePath = ResolveVideoPath(videoArg);
        Debug.Log("VIDEO_DIAG_START|" + sourcePath);
        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        if (!File.Exists(sourcePath))
        {
            Fail("source file missing: " + sourcePath, 2);
            yield break;
        }

        bool encrypted = ShouldDecryptFile(sourcePath);
        Debug.Log("VIDEO_DIAG_SOURCE|size=" + new FileInfo(sourcePath).Length + "|encrypted=" + encrypted);
        preparedPath = encrypted ? PrepareDecryptedVideo(sourcePath) : sourcePath;
        if (string.IsNullOrEmpty(preparedPath) || !File.Exists(preparedPath))
        {
            Fail("prepared file missing", 3);
            yield break;
        }
        if (!IsPlainMp4(preparedPath))
        {
            Fail("prepared file is not a valid mp4 header: " + preparedPath, 4);
            yield break;
        }

        GameObject playerObject = new GameObject("VideoDiagnosticPlayer");
        AudioSource audio = playerObject.AddComponent<AudioSource>();
        videoPlayer = playerObject.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.skipOnDrop = true;
        videoPlayer.source = VideoSource.Url;
        videoPlayer.url = preparedPath;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
        videoPlayer.SetTargetAudioSource(0, audio);
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.targetTexture = new RenderTexture(1920, 1080, 0);
        videoPlayer.prepareCompleted += OnPrepared;
        videoPlayer.errorReceived += OnError;
        videoPlayer.loopPointReached += OnEnded;

        Debug.Log("VIDEO_DIAG_PREPARE|" + preparedPath);
        videoPlayer.Prepare();

        float prepareStart = Time.realtimeSinceStartup;
        while (!prepared && !failed && Time.realtimeSinceStartup - prepareStart < 30f)
        {
            yield return null;
        }

        if (!prepared && !failed)
        {
            Fail("prepare timeout", 5);
            yield break;
        }

        while (!failed && Time.realtimeSinceStartup - startedAt < durationSeconds)
        {
            yield return new WaitForSeconds(sampleIntervalSeconds);
            SamplePlayback();
            if (videoPlayer.isPlaying && Time.realtimeSinceStartup - lastProgressAt > 5f)
            {
                Fail("playback stalled for more than 5 seconds", 6);
                yield break;
            }
        }

        if (!failed)
        {
            LogProgress();
            Debug.Log("VIDEO_DIAG_SUCCESS");
            CleanupPreparedVideo();
            Application.Quit(0);
        }
    }

    private void OnPrepared(VideoPlayer source)
    {
        prepared = true;
        startedAt = Time.realtimeSinceStartup;
        lastProgressAt = startedAt;
        lastSampleAt = startedAt;
        lastFrameAdvanceAt = startedAt;
        lastLogAt = startedAt;
        maxFrameHoldSeconds = 0f;
        lastVideoTime = source.time;
        lastFrame = source.frame;
        Debug.Log("VIDEO_DIAG_PREPARED|length=" + source.length + "|frameCount=" + source.frameCount + "|frameRate=" + source.frameRate);
        source.Play();
    }

    private void OnError(VideoPlayer source, string message)
    {
        Fail("VideoPlayer error: " + message, 10);
    }

    private void OnEnded(VideoPlayer source)
    {
        Debug.Log("VIDEO_DIAG_ENDED|clock=" + (Time.realtimeSinceStartup - startedAt).ToString("F1")
            + "|time=" + source.time.ToString("F3")
            + "|frame=" + source.frame
            + "|maxFrameHold=" + maxFrameHoldSeconds.ToString("F3"));
        CleanupPreparedVideo();
        Application.Quit(0);
    }

    private void SamplePlayback()
    {
        if (videoPlayer == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        double time = videoPlayer.time;
        long frame = videoPlayer.frame;

        if (lastVideoTime > 1.0 && time + 0.2 < lastVideoTime)
        {
            Fail("video time moved backwards from " + lastVideoTime.ToString("F3") + " to " + time.ToString("F3"), 11);
            return;
        }

        if (lastFrame >= 0 && frame >= 0 && frame + 3 < lastFrame)
        {
            Fail("video frame moved backwards from " + lastFrame + " to " + frame, 12);
            return;
        }

        bool advancedFrame = frame > lastFrame;
        bool advancedTime = time > lastVideoTime + 0.02;
        if (advancedFrame || advancedTime)
        {
            lastProgressAt = now;
            lastFrameAdvanceAt = now;
            lastVideoTime = time;
            lastFrame = frame;
        }
        else if (videoPlayer.isPlaying && frame >= 0)
        {
            float heldFor = now - lastFrameAdvanceAt;
            if (heldFor > maxFrameHoldSeconds)
            {
                maxFrameHoldSeconds = heldFor;
            }
            if (heldFor >= stutterThresholdSeconds)
            {
                Fail("frame did not advance for " + heldFor.ToString("F3") + " seconds at time=" + time.ToString("F3") + " frame=" + frame, 13);
                return;
            }
        }

        if (now - lastLogAt >= logIntervalSeconds)
        {
            lastLogAt = now;
            LogProgress();
        }

        lastSampleAt = now;
    }

    private void LogProgress()
    {
        double time = videoPlayer.time;
        long frame = videoPlayer.frame;
        if (time > lastVideoTime + 0.05 || frame > lastFrame)
        {
            lastProgressAt = Time.realtimeSinceStartup;
            lastVideoTime = time;
            lastFrame = frame;
        }

        Debug.Log("VIDEO_DIAG_PROGRESS|clock=" + (Time.realtimeSinceStartup - startedAt).ToString("F1")
            + "|time=" + time.ToString("F3")
            + "|frame=" + frame
            + "|playing=" + videoPlayer.isPlaying
            + "|prepared=" + videoPlayer.isPrepared
            + "|maxFrameHold=" + maxFrameHoldSeconds.ToString("F3")
            + "|lastSample=" + lastSampleAt.ToString("F3"));
    }

    private static string ResolveVideoPath(string videoArg)
    {
        if (Path.IsPathRooted(videoArg))
        {
            return videoArg;
        }

        return Path.GetFullPath(Path.Combine(Application.dataPath, "../mdsk_external/Videos", videoArg));
    }

    private static bool ShouldDecryptFile(string path)
    {
        byte[] header = ReadHeader(path);
        if (HeaderContainsFtyp(header))
        {
            return false;
        }

        return HeaderContainsFtyp(XorBytes(header));
    }

    private static string PrepareDecryptedVideo(string path)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MDSK_360_VideoDiagnostics");
        Directory.CreateDirectory(directory);
        FileInfo info = new FileInfo(path);
        string safeName = string.Join("_", Path.GetFileName(path).Split(Path.GetInvalidFileNameChars()));
        string output = Path.Combine(directory, safeName + "_" + info.Length.ToString("x") + "_" + info.LastWriteTimeUtc.Ticks.ToString("x") + ".mp4");
        if (File.Exists(output) && IsPlainMp4(output))
        {
            return output;
        }

        string partial = output + ".part";
        byte[] buffer = new byte[4 * 1024 * 1024];
        using (FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream dest = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    buffer[i] ^= Key[i % Key.Length];
                }
                dest.Write(buffer, 0, read);
            }
        }
        if (File.Exists(output))
        {
            File.Delete(output);
        }
        File.Move(partial, output);
        return output;
    }

    private static bool IsPlainMp4(string path)
    {
        return HeaderContainsFtyp(ReadHeader(path));
    }

    private static byte[] ReadHeader(string path)
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

    private static byte[] XorBytes(byte[] data)
    {
        byte[] output = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            output[i] = (byte)(data[i] ^ Key[i % Key.Length]);
        }
        return output;
    }

    private static bool HeaderContainsFtyp(byte[] header)
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

    private static bool HasArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetArgValue(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return fallback;
    }

    private void Fail(string message, int exitCode)
    {
        failed = true;
        Debug.LogError("VIDEO_DIAG_FAIL|" + message);
        CleanupPreparedVideo();
        Application.Quit(exitCode);
    }

    private void CleanupPreparedVideo()
    {
        if (!deletePreparedOnExit || string.IsNullOrEmpty(preparedPath) || preparedPath == sourcePath)
        {
            return;
        }

        try
        {
            if (File.Exists(preparedPath))
            {
                File.Delete(preparedPath);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("VIDEO_DIAG_CLEANUP_FAIL|" + preparedPath + "|" + ex.Message);
        }
    }
}

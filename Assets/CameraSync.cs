using FFmpegOut;
using UnityEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using UnityEngine.Rendering;

public class CameraSync : MonoBehaviour
{
    public Camera mainCamera;
    public Camera externalCamera;
    public GameObject objectExternalVideoLoader;

    private ExternalVideoLoader scriptExternalVideoLoader;
    private readonly List<CameraCapture> cameraCaptureInstances = new List<CameraCapture>();
    private readonly List<Camera> captureCameras = new List<Camera>();

    private MemoryMappedFile mmf;
    private MemoryMappedViewAccessor accessor;
    private int frameSize;

    private const float PreviewWriteInterval = 1.0f / 20.0f;
    private float timeSinceLastWrite = 0.0f;

    private struct FrameReadbackData
    {
        public byte[] RawData;
        public int Width;
        public int Height;
    }

    private Thread memoryWriteThread;
    private BlockingCollection<FrameReadbackData> frameQueue = new BlockingCollection<FrameReadbackData>(new ConcurrentQueue<FrameReadbackData>());
    private volatile bool isRunning = true;
    private byte[] resizedFlippedData;
    private int previousWidth = 0;
    private int previousHeight = 0;
    private bool loggedPreviewFailure = false;
    private RenderTexture previewRenderTexture;
    private bool externalCameraEnabledBeforeRecording;
    private bool externalCameraStateCaptured;

    void Start()
    {
        scriptExternalVideoLoader = objectExternalVideoLoader.GetComponent<ExternalVideoLoader>();
        if (scriptExternalVideoLoader != null)
        {
            scriptExternalVideoLoader.OnRecordingSignalSent += HandleRecordingSignal;
            scriptExternalVideoLoader.OnStopRecordingSignalSent += HandleStopSignal;
            scriptExternalVideoLoader.OnSettingMemoryMappedFileSignalSent += HandleSettingMemoryMappedFileSignal;
        }

        memoryWriteThread = new Thread(MemoryWriteWorker);
        memoryWriteThread.IsBackground = true;
        memoryWriteThread.Start();
    }

    void Update()
    {
        timeSinceLastWrite += Time.deltaTime;

        if (mainCamera != null && captureCameras.Count > 0)
        {
            foreach (var captureCamera in captureCameras)
            {
                if (captureCamera == null) continue;
                captureCamera.transform.position = mainCamera.transform.position;
                captureCamera.transform.rotation = mainCamera.transform.rotation;
                captureCamera.fieldOfView = 102f;
            }
        }

        if (mainCamera != null && externalCamera != null)
        {
            externalCamera.transform.position = mainCamera.transform.position;
            externalCamera.transform.rotation = mainCamera.transform.rotation;
            externalCamera.fieldOfView = 102f;
            externalCamera.nearClipPlane = mainCamera.nearClipPlane;
            externalCamera.farClipPlane = mainCamera.farClipPlane;
        }

        if (timeSinceLastWrite >= PreviewWriteInterval && accessor != null && frameQueue.Count < 2)
        {
            var renderTexture = externalCamera != null ? externalCamera.targetTexture : null;
            if (renderTexture != null)
            {
                if (!externalCamera.enabled && externalCamera.gameObject.activeInHierarchy)
                {
                    externalCamera.Render();
                }
                AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32, OnCompleteReadback);
            }
            timeSinceLastWrite = 0.0f;
        }
    }

    void OnCompleteReadback(AsyncGPUReadbackRequest request)
    {
        if (request.hasError)
        {
            Debug.LogWarning("GPU readback error detected.");
            return;
        }

        if (frameQueue.IsAddingCompleted)
        {
            return;
        }

        frameQueue.Add(new FrameReadbackData
        {
            RawData = request.GetData<byte>().ToArray(),
            Width = request.width,
            Height = request.height
        });
    }

    private void MemoryWriteWorker()
    {
        while (isRunning)
        {
            try
            {
                var frame = frameQueue.Take();
                WritePreviewFrame(frame);
                loggedPreviewFailure = false;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!loggedPreviewFailure)
                {
                    Debug.LogWarning("VR preview shared memory write failed; continuing playback. " + ex.Message);
                    loggedPreviewFailure = true;
                }
            }
        }
    }

    private void WritePreviewFrame(FrameReadbackData frame)
    {
        if (accessor == null || frame.Width <= 1 || frame.Height <= 1)
        {
            return;
        }

        byte[] rawData = frame.RawData;
        int expectedBytes = frame.Width * frame.Height * 4;
        if (rawData == null || rawData.Length < expectedBytes)
        {
            Debug.LogWarning("Skipping invalid VR preview frame. width=" + frame.Width + " height=" + frame.Height + " bytes=" + (rawData == null ? 0 : rawData.Length));
            return;
        }

        int newWidth = frame.Width / 2;
        int newHeight = frame.Height / 2;
        int outputBytes = newWidth * newHeight * 4;
        if (resizedFlippedData == null || newWidth != previousWidth || newHeight != previousHeight)
        {
            resizedFlippedData = new byte[outputBytes];
            previousWidth = newWidth;
            previousHeight = newHeight;
        }

        for (int y = 0; y < newHeight; y++)
        {
            int oldY = (newHeight - 1 - y) * 2;
            for (int x = 0; x < newWidth; x++)
            {
                int oldX = x * 2;
                int oldIndex = (oldY * frame.Width + oldX) * 4;
                int newIndex = (y * newWidth + x) * 4;

                resizedFlippedData[newIndex] = rawData[oldIndex];
                resizedFlippedData[newIndex + 1] = rawData[oldIndex + 1];
                resizedFlippedData[newIndex + 2] = rawData[oldIndex + 2];
                resizedFlippedData[newIndex + 3] = rawData[oldIndex + 3];
            }
        }

        if (outputBytes + 8 > frameSize)
        {
            Debug.LogWarning("Skipping oversized VR preview frame. bytes=" + outputBytes + " frameSize=" + frameSize);
            return;
        }

        accessor.Write(0, newWidth);
        accessor.Write(4, newHeight);
        accessor.WriteArray(8, resizedFlippedData, 0, outputBytes);
    }

    void HandleRecordingSignal(string outputPath)
    {
        Debug.Log("Screen Capture Start: " + outputPath);

        if (mainCamera == null || externalCamera == null)
        {
            return;
        }

        // Defensive cleanup in case a prior segment did not fully tear down.
        if (cameraCaptureInstances.Count > 0 || captureCameras.Count > 0)
        {
            HandleStopSignal();
        }

        GameObject captureObject = new GameObject("CameraCaptureObject");
        Camera captureCamera = captureObject.AddComponent<Camera>();
        captureCamera.CopyFrom(mainCamera);

        int captureWidth = mainCamera.pixelWidth / 2;
        int captureHeight = mainCamera.pixelHeight / 2;
        captureCamera.targetTexture = new RenderTexture(captureWidth, captureHeight, 24);

        ReleaseRenderTexture(previewRenderTexture);
        previewRenderTexture = new RenderTexture(mainCamera.pixelWidth / 2, mainCamera.pixelHeight / 2, 24);
        externalCamera.targetTexture = previewRenderTexture;
        externalCameraEnabledBeforeRecording = externalCamera.enabled;
        externalCameraStateCaptured = true;
        externalCamera.enabled = false;

        CameraCapture newCapture = captureObject.AddComponent<CameraCapture>();
        newCapture.width = captureWidth;
        newCapture.height = captureHeight;
        newCapture.frameRate = 20;
        newCapture.preset = FFmpegPreset.H264Nvidia;
        newCapture.outputPath = outputPath;
        newCapture.manualCameraRender = true;

        captureCamera.enabled = false;
        cameraCaptureInstances.Add(newCapture);
        captureCameras.Add(captureCamera);
    }

    void HandleStopSignal()
    {
        foreach (var capture in cameraCaptureInstances)
        {
            if (capture != null)
            {
                capture.StopRecording();
                var camera = capture.GetComponent<Camera>();
                if (camera != null)
                {
                    ReleaseRenderTexture(camera.targetTexture);
                    camera.targetTexture = null;
                }
                Destroy(capture.gameObject);
            }
        }
        cameraCaptureInstances.Clear();
        captureCameras.Clear();

        if (externalCamera != null)
        {
            externalCamera.targetTexture = null;
            if (externalCameraStateCaptured)
            {
                externalCamera.enabled = externalCameraEnabledBeforeRecording;
                externalCameraStateCaptured = false;
            }
        }
        ReleaseRenderTexture(previewRenderTexture);
        previewRenderTexture = null;
    }

    void HandleSettingMemoryMappedFileSignal(string sharedMemoryName)
    {
        // Python preview reader in D:\vader2 assumes a 617x685 RGBA frame payload.
        // We still allocate/write width/height in the header, but the backing MMF must
        // remain compatible with that legacy capacity calculation.
        frameSize = 1234 * 1370 + 8;

        try
        {
            mmf = MemoryMappedFile.OpenExisting(sharedMemoryName);
            mmf.Dispose();
            mmf = MemoryMappedFile.CreateNew(sharedMemoryName, frameSize);
            Debug.Log("Found existing MemoryMappedFile.");
        }
        catch (FileNotFoundException)
        {
            Debug.Log("Did not find existing MemoryMappedFile.");
            mmf = MemoryMappedFile.CreateNew(sharedMemoryName, frameSize);
        }

        accessor?.Dispose();
        accessor = mmf.CreateViewAccessor();
    }

    void OnDestroy()
    {
        HandleStopSignal();
        isRunning = false;
        if (frameQueue != null && !frameQueue.IsAddingCompleted)
        {
            frameQueue.CompleteAdding();
        }
        if (memoryWriteThread != null && memoryWriteThread.IsAlive)
        {
            memoryWriteThread.Join(1000);
        }

        accessor?.Dispose();
        mmf?.Dispose();
    }

    private void ReleaseRenderTexture(RenderTexture renderTexture)
    {
        if (renderTexture == null)
        {
            return;
        }

        renderTexture.Release();
        Destroy(renderTexture);
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

#if UNITY_STANDALONE_WIN || UNITY_WSA
using UnityEngine.Windows.Speech;
#endif

#if WINDOWS_UWP
using Windows.System;
#endif

namespace Microsoft.MixedReality.Profiling
{
    /// <summary>
    /// 
    /// ABOUT: The VisualProfiler provides a drop in solution for viewing your Mixed Reality 
    /// Unity application's frame rate and memory usage. Missed frames are displayed over time to 
    /// visually find problem areas. Draw calls, batches, and vertex (or triangle) counts are displayed to 
    /// diagnose scene complexity. Memory is reported as current, peak and max usage in a bar graph.
    /// 
    /// USAGE: To use this profiler simply add this script as a component of any GameObject in 
    /// your Unity scene. The profiler is initially active and visible (toggle-able via the 
    /// IsVisible property), but can be toggled via the enabled/disable voice commands keywords (in Windows/UWP).
    /// 
    /// IMPORTANT: Please make sure to add the microphone capability to your UWP app if you plan 
    /// on using the enable/disable keywords, in Unity under Edit -> Project Settings -> 
    /// Player -> Settings for Windows Store -> Publishing Settings -> Capabilities or in your 
    /// Visual Studio Package.appxmanifest capabilities.
    /// 
    /// </summary>
    public sealed class VisualProfiler : MonoBehaviour, ISerializationCallbackReceiver
    {
        [Header("Profiler Settings")]
        [SerializeField, Tooltip("Is the profiler currently visible? If disabled, prevents the profiler from rendering but still allows it to track memory usage.")]
        private bool isVisible = true;

        /// <summary>
        /// Is the profiler currently visible? If disabled, prevents the profiler from rendering but still allows it to track memory usage.
        /// </summary>
        public bool IsVisible
        {
            get { return isVisible; }
            set { isVisible = value; }
        }

        [SerializeField, Tooltip("The amount of time, in seconds, to collect frames before frame rate averaging.")]
        private float frameSampleRate = 0.3f;

        /// <summary>
        /// The amount of time, in seconds, to collect frames before frame rate averaging.
        /// </summary>
        public float FrameSampleRate
        {
            get { return frameSampleRate; }
            set 
            { 
                frameSampleRate = value;
                frameSampleRateMS = frameSampleRate * 1000.0f;
            }
        }

        [SerializeField, Tooltip("What frame rate should the app target if one cannot be determined by the XR device.")]
        private float defaultFrameRate = 60.0f;

        /// <summary>
        /// What frame rate should the app target if one cannot be determined by the XR device.
        /// </summary>
        public float DefaultFrameRate
        {
            get { return defaultFrameRate; }
            set { defaultFrameRate = value; }
        }

        [Header("Window Settings")]
        [SerializeField, Tooltip("What part of the view port to anchor the window to.")]
        private TextAnchor windowAnchor = TextAnchor.LowerCenter;

        /// <summary>
        /// What part of the view port to anchor the window to.
        /// </summary>
        public TextAnchor WindowAnchor
        {
            get { return windowAnchor; }
            set { windowAnchor = value; }
        }

        [SerializeField, Tooltip("The offset from the view port center applied based on the window anchor selection.")]
        private Vector2 windowOffset = new Vector2(0.1f, 0.1f);

        /// <summary>
        /// The offset from the view port center applied based on the window anchor selection.
        /// </summary>
        public Vector2 WindowOffset
        {
            get { return windowOffset; }
            set { windowOffset = value; }
        }

        [SerializeField, Range(0.5f, 5.0f), Tooltip("Use to scale the window size up or down, can simulate a zooming effect.")]
        private float windowScale = 1.0f;

        /// <summary>
        /// Use to scale the window size up or down, can simulate a zooming effect.
        /// </summary>
        public float WindowScale
        {
            get { return windowScale; }
            set { windowScale = Mathf.Clamp(value, 0.5f, 5.0f); }
        }

        [SerializeField, Range(0.0f, 100.0f), Tooltip("How quickly to interpolate the window towards its target position and rotation.")]
        private float windowFollowSpeed = 5.0f;

        /// <summary>
        /// How quickly to interpolate the window towards its target position and rotation.
        /// </summary>
        public float WindowFollowSpeed
        {
            get { return windowFollowSpeed; }
            set { windowFollowSpeed = Mathf.Abs(value); }
        }

        [SerializeField, Tooltip("Should the window snap to location rather than interpolate?")]
        private bool snapWindow = false;

        /// <summary>
        /// Should the window snap to location rather than interpolate?
        /// </summary>
        public bool SnapWindow
        {
            get { return snapWindow; }
            set { snapWindow = value; }
        }

        /// <summary>
        /// Access the CPU frame rate (in frames per second).
        /// </summary>
        public float SmoothCpuFrameRate { get; private set; }

        /// <summary>
        /// Access the GPU frame rate (in frames per second). Will return zero when GPU profiling is not available.
        /// </summary>
        public float SmoothGpuFrameRate { get; private set; }

        /// <summary>
        /// Returns the target frame rate for the current platform.
        /// </summary>
        public float TargetFrameRate
        {
            get
            {
                // If the current XR SDK does not report refresh rate information, assume 60Hz.
                float refreshRate = 0;
#if ENABLE_VR
                refreshRate = UnityEngine.XR.XRDevice.refreshRate;
#endif
                return ((int)refreshRate == 0) ? defaultFrameRate : refreshRate;
            }
        }

        /// <summary>
        /// Returns the target frame time in milliseconds for the current platform.
        /// </summary>
        public float TargetFrameTime
        {
            get
            {
                return (1.0f / TargetFrameRate) * 1000.0f;
            }
        }

        [SerializeField, Tooltip("Voice commands to toggle the profiler on and off. (Supported in UWP only.)")]
        private string[] toggleKeyworlds = new string[] { "Profiler", "Toggle Profiler", "Show Profiler", "Hide Profiler" };

        [Header("UI Settings")]
        [SerializeField, Tooltip("The material to use when rendering the profiler. The material should use the \"Hidden / Visual Profiler\" shader and have a font texture.")]
        private Material material;

        [SerializeField, Range(0, 2), Tooltip("How many decimal places to display on numeric strings.")]
        private int displayedDecimalDigits = 1;

        [SerializeField, Tooltip("Display triangle count instead of vertex count in the profiler.")]
        private bool displayTriangleCount = false;

        [SerializeField, Tooltip("The color of the window backplate.")]
        private Color baseColor = new Color(50 / 255.0f, 50 / 255.0f, 50 / 255.0f, 1.0f);

        [SerializeField, Tooltip("The color to display on frames which meet or exceed the target frame rate.")]
        private Color targetFrameRateColor = new Color(127 / 255.0f, 186 / 255.0f, 0 / 255.0f, 1.0f);

        [SerializeField, Tooltip("The color to display on frames which fall below the target frame rate.")]
        private Color missedFrameRateColor = new Color(242 / 255.0f, 80 / 255.0f, 34 / 255.0f, 1.0f);

        [SerializeField, Tooltip("The color to display for current memory usage values.")]
        private Color memoryUsedColor = new Color(0 / 255.0f, 164 / 255.0f, 239 / 255.0f, 1.0f);

        [SerializeField, Tooltip("The color to display for peak (aka max) memory usage values.")]
        private Color memoryPeakColor = new Color(255 / 255.0f, 185 / 255.0f, 0 / 255.0f, 1.0f);

        [SerializeField, Tooltip("The color to display for the platforms memory usage limit.")]
        private Color memoryLimitColor = new Color(100 / 255.0f, 100 / 255.0f, 100 / 255.0f, 1.0f);

        [Header("Font Settings")]
        [SerializeField, Tooltip("The width and height of a mono spaced character in the font texture (in pixels).")]
        private Vector2Int fontCharacterSize = new Vector2Int(16, 30);

        [SerializeField, Tooltip("The x and y scale to render a character at.")]
        private Vector2 fontScale = new Vector2(0.00023f, 0.00028f);

        [SerializeField, Min(1), Tooltip("How many characters are in a row of the font texture.")]
        private int fontColumns = 32;

        [Serializable]
        private class CustomProfiler
        {
            public enum Category
            {
                None = 0,
                VirtualTexturing,
                Memory,
                Input,
                Vr,
                Loading,
                Network,
                Lighting,
                Particles,
                Video,
                Audio,
                Ai,
                Animation,
                Physics,
                Gui,
                Scripts,
                Render,
                FileIO,
                Internal,
            }

            [Tooltip("The visible name of the stat in the profiler. This should be no longer than 9 characters.")]
            public string DisplayName;

            [Tooltip("The category to pass to ProfilerRecorder.StartNew. If \"None\" is specified ProfilerRecorder.StartNew will be invoked with a new ProfilerMarker named StatName.")]
            public Category CategoryType = Category.None;
            
            [Tooltip("Profiler marker or counter name.")]
            public string StatName;

            [Min(1), Tooltip("The amount of samples to collect then average when displaying the profiler marker.")]
            public int SampleCapacity = 300;

            [Range(0.0f, 1.0f), Tooltip("What % of the target frame time can this profiler take before being considered over budget?")]
            public float BudgetPercentage = 1.0f;

            public TextData Text { get; set; }
            public float LastValuePresented { get; set; }

            private bool hasEverPresented = false;
            private bool running = false;
            private ProfilerRecorder recorder;

            public void Start()
            {
                if (CategoryType == Category.None)
                {
                    recorder = ProfilerRecorder.StartNew(new ProfilerMarker(StatName), SampleCapacity);
                }
                else
                {
                    recorder = ProfilerRecorder.StartNew(ToProfilerCategory(CategoryType), StatName, SampleCapacity);
                }

                running = true;
            }

            public void Stop()
            {
                recorder.Dispose();

                running = false;
            }

            public void Reset()
            {
                hasEverPresented = false;
                LastValuePresented = -1.0f;
            }

            public bool ReadyToPresent()
            {
                return running && (recorder.Count == SampleCapacity) || (hasEverPresented == false);
            }

            public void Present(float value)
            {
                hasEverPresented = true;
                LastValuePresented = value;
            }

            public float CalculateAverage()
            {
                if (!running)
                {
                    return 0.0f;
                }

                long sum = 0;
                int length = 0;
                int count = recorder.Count;

                for (int i = 0; i < count; ++i)
                {
                    var value = recorder.GetSample(i).Value;

                    if (value == 0)
                    {
                        continue;
                    }

                    sum += value;
                    ++length;
                }

                return (length > 0) ? (float)sum / length : 0.0f;
            }

            private static ProfilerCategory ToProfilerCategory(Category category)
            {
                switch (category)
                {
                    default:
                    case Category.None:             return new ProfilerCategory();
                    case Category.VirtualTexturing: return ProfilerCategory.VirtualTexturing;
                    case Category.Memory:           return ProfilerCategory.Memory;
                    case Category.Input:            return ProfilerCategory.Input;
                    case Category.Vr:               return ProfilerCategory.Vr;
                    case Category.Loading:          return ProfilerCategory.Loading;
                    case Category.Network:          return ProfilerCategory.Network;
                    case Category.Lighting:         return ProfilerCategory.Lighting;
                    case Category.Particles:        return ProfilerCategory.Particles;
                    case Category.Video:            return ProfilerCategory.Video;
                    case Category.Audio:            return ProfilerCategory.Audio;
                    case Category.Ai:               return ProfilerCategory.Ai;
                    case Category.Animation:        return ProfilerCategory.Animation;
                    case Category.Physics:          return ProfilerCategory.Physics;
                    case Category.Gui:              return ProfilerCategory.Gui;
                    case Category.Scripts:          return ProfilerCategory.Scripts;
                    case Category.Render:           return ProfilerCategory.Render;
                    case Category.FileIO:           return ProfilerCategory.FileIO;
                    case Category.Internal:         return ProfilerCategory.Internal;
                }
            }
        }

        [SerializeField, Tooltip("Populate this list with ProfilerRecorder profiler markers to display timing information.")]
        private CustomProfiler[] customProfilers = new CustomProfiler[0];

        // Constants.
        private const int maxStringLength = 17;
        private const int maxTargetFrameRate = 240;
        private const int frameRange = 30;

        // These offsets specify how many instances a portion of the UI uses as well as draw order. 
        private const int backplateInstanceOffset = 0;

        private const int framesInstanceOffset = backplateInstanceOffset + 1;

        private const int limitInstanceOffset = framesInstanceOffset + frameRange;
        private const int peakInstanceOffset = limitInstanceOffset + 1;
        private const int usedInstanceOffset = peakInstanceOffset + 1;

        private const int cpuframeRateTextOffset = usedInstanceOffset + 1;
        private const int gpuframeRateTextOffset = cpuframeRateTextOffset + maxStringLength;

        private const int batchesTextOffset = gpuframeRateTextOffset + maxStringLength;
        private const int drawCallTextOffset = batchesTextOffset + maxStringLength;
        private const int meshStatsTextOffset = drawCallTextOffset + maxStringLength;

        private const int usedMemoryTextOffset = meshStatsTextOffset + maxStringLength;
        private const int limitMemoryTextOffset = usedMemoryTextOffset + maxStringLength;
        private const int peakMemoryTextOffset = limitMemoryTextOffset + maxStringLength;

        private const int lastOffset = peakMemoryTextOffset + maxStringLength;

        private static readonly int colorID = Shader.PropertyToID("_Color");
        private static readonly int baseColorID = Shader.PropertyToID("_BaseColor");
        private static readonly int fontScaleID = Shader.PropertyToID("_FontScale");
        private static readonly int uvOffsetScaleXID = Shader.PropertyToID("_UVOffsetScaleX");
        private static readonly int windowLocalToWorldID = Shader.PropertyToID("_WindowLocalToWorldMatrix");

        // Pre computed state.
        private char[][] frameRateStrings = new char[maxTargetFrameRate + 1][];
        private char[][] gpuFrameRateStrings = new char[maxTargetFrameRate + 1][];
        private Vector4[] characterUVs = new Vector4[128];
        private Vector3 characterScale = Vector3.zero;

        // UI state.
        private Vector3 windowPosition = Vector3.zero;
        private Quaternion windowRotation = Quaternion.identity;

        private class TextData
        {
            public string Prefix;
            public Vector3 Position;
            public bool RightAligned;
            public int Offset;
            public int LastProcessed;

            public TextData(Vector3 position, bool rightAligned, int offset, string prefix = "")
            {
                Position = position;
                RightAligned = rightAligned;
                Offset = offset;
                Prefix = prefix;
                LastProcessed = maxStringLength;
            }
        }

        private TextData cpuFrameRateText = null;
        private TextData gpuFrameRateText = null;

        private TextData batchesText = null;
        private TextData drawCallText = null;
        private TextData meshText = null;

        private TextData usedMemoryText = null;
        private TextData peakMemoryText = null;
        private TextData limitMemoryText = null;

        private Quaternion windowHorizontalRotation = Quaternion.identity;
        private Quaternion windowHorizontalRotationInverse = Quaternion.identity;
        private Quaternion windowVerticalRotation = Quaternion.identity;
        private Quaternion windowVerticalRotationInverse = Quaternion.identity;

        private char[] stringBuffer = new char[maxStringLength];

        private int cpuFrameRate = -1;
        private int gpuFrameRate = -1;
        private long batches = 0;
        private long drawCalls = 0;
        private long meshStatsCount = 0;
        private ulong memoryUsage = 0;
        private ulong peakMemoryUsage = 0;
        private ulong limitMemoryUsage = 0;
        private bool peakMemoryUsageDirty = false;

        // Profiling state.
        private int frameCount = 0;
        private float accumulatedFrameTimeCPU = 0.0f;
        private float accumulatedFrameTimeGPU = 0.0f;
        private float frameSampleRateMS = 0.0f;
        private FrameTiming[] frameTimings = new FrameTiming[1];
        private ProfilerRecorder batchesRecorder;
        private ProfilerRecorder drawCallsRecorder;
        private ProfilerRecorder meshStatsRecorder;

        // Rendering state.
        private Mesh quadMesh;
        private MaterialPropertyBlock instancePropertyBlock;
        private Matrix4x4[] instanceMatrices;
        private Vector4[] instanceColors;
        private Vector4[] instanceBaseColors;
        private Vector4[] instanceUVOffsetScaleX;
        private bool instanceColorsDirty = false;
        private bool instanceBaseColorsDirty = false;
        private bool instanceUVOffsetScaleXDirty = false;

        /// <summary>
        /// Reset any stats the profiler is tracking. Call this if you would like to restart tracking 
        /// statistics like peak memory usage.
        /// </summary>
        public void Refresh()
        {
            SmoothCpuFrameRate = 0.0f;
            SmoothGpuFrameRate = 0.0f;
            cpuFrameRate = -1;
            gpuFrameRate = -1;
            batches = 0;
            drawCalls = 0;
            meshStatsCount = 0;
            memoryUsage = 0;
            peakMemoryUsage = 0;
            limitMemoryUsage = 0;
        }

        private void OnEnable()
        {
            if (material == null)
            {
                Debug.LogError("The VisualProfiler is missing a material and will not display.");
            }

            // Create a quad mesh with artificially large bounds to disable culling for instanced rendering.
            // TODO - [Cameron-Micka] Use shared mesh with normal bounds once Unity allows for more control over instance culling.
            if (quadMesh == null)
            {
                MeshFilter quadMeshFilter = GameObject.CreatePrimitive(PrimitiveType.Quad).GetComponent<MeshFilter>();
                quadMesh = quadMeshFilter.mesh;
                quadMesh.bounds = new Bounds(Vector3.zero, Vector3.one * float.MaxValue);

                Destroy(quadMeshFilter.gameObject);
            }

            instancePropertyBlock = new MaterialPropertyBlock();

            Vector2 defaultWindowRotation = new Vector2(10.0f, 20.0f);

            windowHorizontalRotation = Quaternion.AngleAxis(defaultWindowRotation.y, Vector3.right);
            windowHorizontalRotationInverse = Quaternion.Inverse(windowHorizontalRotation);
            windowVerticalRotation = Quaternion.AngleAxis(defaultWindowRotation.x, Vector3.up);
            windowVerticalRotationInverse = Quaternion.Inverse(windowVerticalRotation);

            batchesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            drawCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            meshStatsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, displayTriangleCount ? "Triangles Count" : "Vertices Count");

            foreach (var profiler in customProfilers)
            {
                profiler.Start();
            }

            BuildWindow();

#if UNITY_STANDALONE_WIN || UNITY_WSA
            BuildKeywordRecognizer();
#endif
        }

        private void OnDisable()
        {
#if UNITY_STANDALONE_WIN || UNITY_WSA
            if (keywordRecognizer != null && keywordRecognizer.IsRunning)
            {
                keywordRecognizer.Stop();
                keywordRecognizer.Dispose();
                keywordRecognizer = null;
            }
#endif

            foreach (var profiler in customProfilers)
            {
                profiler.Stop();
            }

            meshStatsRecorder.Dispose();
            drawCallsRecorder.Dispose();
            batchesRecorder.Dispose();
        }

        private void OnValidate()
        {
            BuildWindow();
        }

        public void OnBeforeSerialize()
        {
            // Default values for serializable classes in arrays/lists are not supported. This ensures correct construction.
            for (int i = 0; i < customProfilers.Length; ++i)
            {
                if (customProfilers[i].SampleCapacity == 0)
                {
                    customProfilers[i] = new CustomProfiler();
                }
            }
        }

        public void OnAfterDeserialize()
        {
        }

        private void LateUpdate()
        {
            if (IsVisible)
            {
                // Update window transformation.
                Transform cameraTransform = Camera.main ? Camera.main.transform : null;

                if (cameraTransform != null)
                {
                    Vector3 targetPosition = CalculateWindowPosition(cameraTransform);
                    Quaternion targetRotaton = CalculateWindowRotation(cameraTransform);

                    if (snapWindow)
                    {
                        windowPosition = targetPosition;
                        windowRotation = targetRotaton;
                    }
                    else
                    {
                        float t = Time.deltaTime * windowFollowSpeed;
                        windowPosition = Vector3.Lerp(windowPosition, CalculateWindowPosition(cameraTransform), t);
                        // Lerp rather than slerp for speed over quality.
                        windowRotation = Quaternion.Lerp(windowRotation, CalculateWindowRotation(cameraTransform), t);
                    }
                }

                // Many platforms do not yet support the FrameTimingManager. When timing data is returned from the FrameTimingManager we will use
                // its timing data, else we will depend on the deltaTime. Wider support is coming in Unity 2022.1+
                // https://blog.unity.com/technology/detecting-performance-bottlenecks-with-unity-frame-timing-manager
                FrameTimingManager.CaptureFrameTimings();
                uint frameTimingsCount = FrameTimingManager.GetLatestTimings(1, frameTimings);

                if (frameTimingsCount != 0)
                {
                    accumulatedFrameTimeCPU += (float)frameTimings[0].cpuFrameTime;
                    accumulatedFrameTimeGPU += (float)frameTimings[0].gpuFrameTime;
                }
                else
                {
                    accumulatedFrameTimeCPU += Time.unscaledDeltaTime * 1000.0f;
                    // No GPU time to query.
                }

                ++frameCount;

                if (accumulatedFrameTimeCPU >= frameSampleRateMS)
                {
                    if (accumulatedFrameTimeCPU != 0.0f)
                    {
                        SmoothCpuFrameRate = Mathf.Max(1.0f / ((accumulatedFrameTimeCPU * 0.001f) / frameCount), 0.0f);
                    }

                    int lastCpuFrameRate = Mathf.Min(Mathf.RoundToInt(SmoothCpuFrameRate), maxTargetFrameRate);

                    if (accumulatedFrameTimeGPU != 0.0f)
                    {
                        SmoothGpuFrameRate = Mathf.Max(1.0f / ((accumulatedFrameTimeGPU * 0.001f) / frameCount), 0.0f);
                    }

                    int lastGpuFrameRate = Mathf.Min(Mathf.RoundToInt(SmoothGpuFrameRate), maxTargetFrameRate);

                    // TODO - [Cameron-Micka] Ideally we would query a device specific API (like the HolographicFramePresentationReport) to detect missed frames.
                    bool missedFrame = lastCpuFrameRate < ((int)(TargetFrameRate) - 1);
                    Color frameColor = missedFrame ? missedFrameRateColor : targetFrameRateColor;
                    Vector4 frameIcon = missedFrame ? characterUVs['X'] : characterUVs[' '];

                    // Update frame rate text.
                    if (lastCpuFrameRate != cpuFrameRate)
                    {
                        char[] text = frameRateStrings[lastCpuFrameRate];
                        SetText(cpuFrameRateText, text, text.Length, frameColor);
                        cpuFrameRate = lastCpuFrameRate;
                    }

                    if (lastGpuFrameRate != gpuFrameRate)
                    {
                        char[] text = gpuFrameRateStrings[lastGpuFrameRate];
                        Color color = (lastGpuFrameRate < ((int)(TargetFrameRate) - 1)) ? missedFrameRateColor : targetFrameRateColor;
                        SetText(gpuFrameRateText, text, text.Length, color);
                        gpuFrameRate = lastGpuFrameRate;
                    }

                    // Animate frame colors and icons.
                    for (int i = frameRange - 1; i > 0; --i)
                    {
                        int write = framesInstanceOffset + i;
                        int read = framesInstanceOffset + i - 1;
                        instanceBaseColors[write] = instanceBaseColors[read];
                        instanceUVOffsetScaleX[write] = instanceUVOffsetScaleX[read];
                    }

                    instanceBaseColors[framesInstanceOffset + 0] = frameColor;
                    instanceUVOffsetScaleX[framesInstanceOffset + 0] = frameIcon;

                    instanceBaseColorsDirty = true;
                    instanceUVOffsetScaleXDirty = true;

                    // Reset timers.
                    frameCount = 0;
                    accumulatedFrameTimeCPU = 0.0f;
                    accumulatedFrameTimeGPU = 0.0f;
                }

                // Update scene statistics.
                long lastBatches = batchesRecorder.LastValue;

                if (lastBatches != batches)
                {
                    SceneStatsToString(stringBuffer, batchesText, lastBatches);

                    batches = lastBatches;
                }

                long lastDrawCalls = drawCallsRecorder.LastValue;

                if (lastDrawCalls != drawCalls)
                {
                    SceneStatsToString(stringBuffer, drawCallText, lastDrawCalls);

                    drawCalls = lastDrawCalls;
                }

                long lastMeshStatsCount = meshStatsRecorder.LastValue;

                if (lastMeshStatsCount != meshStatsCount)
                {
                    if (WillDisplayedMeshStatsCountDiffer(lastMeshStatsCount, meshStatsCount, displayedDecimalDigits))
                    {
                        MeshStatsToString(stringBuffer, displayedDecimalDigits, meshText, lastMeshStatsCount);
                    }

                    meshStatsCount = lastMeshStatsCount;
                }

                // Update memory statistics.
                ulong limit = AppMemoryUsageLimit;

                if (limit != limitMemoryUsage)
                {
                    if (WillDisplayedMemoryUsageDiffer(limitMemoryUsage, limit, displayedDecimalDigits))
                    {
                        MemoryUsageToString(stringBuffer, displayedDecimalDigits, limitMemoryText, limit, Color.white);
                    }

                    limitMemoryUsage = limit;
                }

                ulong usage = AppMemoryUsage;

                if (usage != memoryUsage)
                {
                    Vector4 offsetScale = instanceUVOffsetScaleX[usedInstanceOffset];
                    offsetScale.z = -1.0f + (float)usage / limitMemoryUsage;
                    instanceUVOffsetScaleX[usedInstanceOffset] = offsetScale;
                    instanceUVOffsetScaleXDirty = true;

                    if (WillDisplayedMemoryUsageDiffer(memoryUsage, usage, displayedDecimalDigits))
                    {
                        MemoryUsageToString(stringBuffer, displayedDecimalDigits, usedMemoryText, usage, memoryUsedColor);
                    }

                    memoryUsage = usage;
                }

                if (memoryUsage > peakMemoryUsage || peakMemoryUsageDirty)
                {
                    Vector4 offsetScale = instanceUVOffsetScaleX[peakInstanceOffset];
                    offsetScale.z = -1.0f + (float)memoryUsage / limitMemoryUsage;
                    instanceUVOffsetScaleX[peakInstanceOffset] = offsetScale;
                    instanceUVOffsetScaleXDirty = true;

                    if (WillDisplayedMemoryUsageDiffer(peakMemoryUsage, memoryUsage, displayedDecimalDigits))
                    {
                        MemoryUsageToString(stringBuffer, displayedDecimalDigits, peakMemoryText, memoryUsage, memoryPeakColor);
                    }

                    peakMemoryUsage = memoryUsage;
                    peakMemoryUsageDirty = false;
                }

                // Update custom profilers
                foreach (var profiler in customProfilers)
                {
                    if (profiler.ReadyToPresent())
                    {
                        float milliseconds = profiler.CalculateAverage() * 1e-6f;

                        if (WillDisplayedMillisecondsDiffer(profiler.LastValuePresented, milliseconds, displayedDecimalDigits))
                        {
                            profiler.Present(milliseconds);

                            float budget = TargetFrameTime * profiler.BudgetPercentage;
                            Color color = milliseconds <= budget ? targetFrameRateColor : missedFrameRateColor;
                            MillisecondsToString(stringBuffer, displayedDecimalDigits, profiler.Text, milliseconds, color);
                        }
                    }
                }

                // Render.
                Matrix4x4 windowLocalToWorldMatrix = Matrix4x4.TRS(windowPosition, windowRotation, Vector3.one * windowScale);
                instancePropertyBlock.SetMatrix(windowLocalToWorldID, windowLocalToWorldMatrix);

                if (instanceColorsDirty)
                {
                    instancePropertyBlock.SetVectorArray(colorID, instanceColors);
                    instanceColorsDirty = false;
                }

                if (instanceBaseColorsDirty)
                {
                    instancePropertyBlock.SetVectorArray(baseColorID, instanceBaseColors);
                    instanceBaseColorsDirty = false;
                }

                if (instanceUVOffsetScaleXDirty)
                {
                    instancePropertyBlock.SetVectorArray(uvOffsetScaleXID, instanceUVOffsetScaleX);
                    instanceUVOffsetScaleXDirty = false;
                }

                if (material != null)
                {
                    Graphics.DrawMeshInstanced(quadMesh, 0, material, instanceMatrices, instanceMatrices.Length, instancePropertyBlock, UnityEngine.Rendering.ShadowCastingMode.Off, false);
                }
            }
            else
            {
                // Keep tracking peak memory usage when not visible.
                ulong usage = AppMemoryUsage;

                if (usage > peakMemoryUsage)
                {
                    peakMemoryUsage = usage;
                    peakMemoryUsageDirty = true;
                }
            }
        }

        private void BuildWindow()
        {
            BuildFrameRateStrings();
            BuildCharacterUVs();

            int instanceCount = lastOffset + (maxStringLength * customProfilers.Length);
            instanceMatrices = new Matrix4x4[instanceCount];
            instanceColors = new Vector4[instanceCount];
            instanceBaseColors = new Vector4[instanceCount];
            instanceUVOffsetScaleX = new Vector4[instanceCount];

            Vector4 spaceUV = characterUVs[' '];

            Vector3 defaultWindowSize = new Vector3(0.2f, 0.04f, 1.0f);
            float edge = defaultWindowSize.x * 0.5f;
            float[] edges = new float[] { -edge, -0.03f, edge };
  
            // Set the default base color.
            for (int i = 0; i < instanceBaseColors.Length; ++i)
            {
                instanceBaseColors[i] = baseColor;
            }

            // Add a window back plate.
            {
                instanceMatrices[backplateInstanceOffset] = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, defaultWindowSize);
                instanceColors[backplateInstanceOffset] = baseColor;
                instanceBaseColors[backplateInstanceOffset] = baseColor;
                instanceUVOffsetScaleX[backplateInstanceOffset] = spaceUV;
            }

            // Add frame rate text.
            {
                float height = 0.02f;
                cpuFrameRateText = new TextData(new Vector3(edges[0], height, 0.0f), false, cpuframeRateTextOffset);
                LayoutText(cpuFrameRateText);
                gpuFrameRateText = new TextData(new Vector3(edges[2], height, 0.0f), true, gpuframeRateTextOffset);
                LayoutText(gpuFrameRateText);
            }

            // Add frame rate indicators.
            {
                float height = 0.008f;
                float size = (1.0f / frameRange) * defaultWindowSize.x;
                Vector3 scale = new Vector3(size, size, 1.0f);
                Vector3 position = new Vector3(-defaultWindowSize.x * 0.5f, height, 0.0f);
                position.x += scale.x * 0.5f;

                for (int i = 0; i < frameRange; ++i)
                {
                    instanceMatrices[framesInstanceOffset + i] = Matrix4x4.TRS(position, Quaternion.identity, new Vector3(scale.x * 0.8f, scale.y * 0.8f, scale.z));
                    position.x += scale.x;
                    instanceColors[framesInstanceOffset + i] = Color.white;
                    instanceBaseColors[framesInstanceOffset + i] = targetFrameRateColor;
                    instanceUVOffsetScaleX[framesInstanceOffset + i] = spaceUV;
                }
            }

            // Add scene statistics text.
            {
                float height = 0.0045f;
                batchesText = new TextData(new Vector3(edges[0], height, 0.0f), false, batchesTextOffset, "Batches: ");
                LayoutText(batchesText);
                drawCallText = new TextData(new Vector3(edges[1], height, 0.0f), false, drawCallTextOffset, "Draw Calls: ");
                LayoutText(drawCallText);
                meshText = new TextData(new Vector3(edges[2], height, 0.0f), true, meshStatsTextOffset, displayTriangleCount ? "Tris: " : "Verts: ");
                LayoutText(meshText);
            }

            // Add memory usage bars.
            {
                float height = -0.0075f;
                Vector3 position = new Vector3(0.0f, height, 0.0f);
                Vector3 scale = defaultWindowSize;
                scale.Scale(new Vector3(0.99f, 0.15f, 1.0f));

                {
                    instanceMatrices[limitInstanceOffset] = Matrix4x4.TRS(position, Quaternion.identity, scale);
                    instanceColors[limitInstanceOffset] = memoryLimitColor;
                    instanceBaseColors[limitInstanceOffset] = memoryLimitColor;
                    instanceUVOffsetScaleX[limitInstanceOffset] = spaceUV;
                }
                {
                    instanceMatrices[peakInstanceOffset] = Matrix4x4.TRS(position, Quaternion.identity, scale);
                    instanceColors[peakInstanceOffset] = memoryPeakColor;
                    instanceBaseColors[peakInstanceOffset] = memoryPeakColor;
                    instanceUVOffsetScaleX[peakInstanceOffset] = spaceUV;
                }
                {
                    instanceMatrices[usedInstanceOffset] = Matrix4x4.TRS(position, Quaternion.identity, scale);
                    instanceColors[usedInstanceOffset] = memoryUsedColor;
                    instanceBaseColors[usedInstanceOffset] = memoryUsedColor;
                    instanceUVOffsetScaleX[usedInstanceOffset] = spaceUV;
                }
            }

            // Add memory usage text.
            {
                float height = -0.011f;
                usedMemoryText = new TextData(new Vector3(edges[0], height, 0.0f), false, usedMemoryTextOffset, "Used: ");
                LayoutText(usedMemoryText);
                peakMemoryText = new TextData(new Vector3(edges[1], height, 0.0f), false, peakMemoryTextOffset, "Peak: ");
                LayoutText(peakMemoryText);
                limitMemoryText = new TextData(new Vector3(edges[2], height, 0.0f), true, limitMemoryTextOffset, "Limit: ");
                LayoutText(limitMemoryText);
            }

            // Add custom profilers.
            {
                int offset = lastOffset;
                float height = -0.02f;

                for (int row = 0; row < customProfilers.Length; row += 3)
                {
                    for (int column = 0; (column < 3) && ((row + column) < customProfilers.Length); ++column)
                    {
                        var profiler = customProfilers[row + column];
                        bool rightAlign = (column == 2) ? true : false;

                        // Allow for at least 8 digits other than the prefix.
                        int maxPrefixLength = maxStringLength - 8;
                        string prefix = (profiler.DisplayName.Length > maxPrefixLength) ? profiler.DisplayName.Substring(0, maxPrefixLength) : profiler.DisplayName;

                        profiler.Text = new TextData(new Vector3(edges[column], height, 0.0f), rightAlign, offset, $"{prefix}: ");
                        LayoutText(profiler.Text);

                        offset += maxStringLength;

                        profiler.Reset();
                    }

                    height -= characterScale.y;
                }
            }

            instanceColorsDirty = true;
            instanceBaseColorsDirty = true;
            instanceUVOffsetScaleXDirty = true;

            // Initialize property block state.
            if (instancePropertyBlock != null && material != null && material.mainTexture != null)
            {
                instancePropertyBlock.SetVector(fontScaleID, new Vector2((float)fontCharacterSize.x / material.mainTexture.width,
                                                                         (float)fontCharacterSize.y / material.mainTexture.height));
            }

            Refresh();
        }

        private void BuildFrameRateStrings()
        {
            frameSampleRateMS = frameSampleRate * 1000.0f;

            string displayedDecimalFormat = string.Format("{{0:F{0}}}", displayedDecimalDigits);

            StringBuilder stringBuilder = new StringBuilder(32);
            StringBuilder milisecondStringBuilder = new StringBuilder(16);

            for (int i = 0; i < frameRateStrings.Length; ++i)
            {
                float miliseconds = (i == 0) ? 0.0f : (1.0f / i) * 1000.0f;
                milisecondStringBuilder.AppendFormat(displayedDecimalFormat, miliseconds);
                string frame = "-", ms = "-.-";

                if (i != 0)
                {
                    frame = i.ToString();
                    ms = milisecondStringBuilder.ToString();
                }

                if (i == (frameRateStrings.Length - 1))
                {
                    stringBuilder.AppendFormat(">{0}fps ({1}ms)", frame, ms);
                }
                else
                {
                    stringBuilder.AppendFormat("{0}fps ({1}ms)", frame, ms);
                }

                frameRateStrings[i] = ToCharArray(stringBuilder);

                stringBuilder.Length = 0;
                

                if (i == (frameRateStrings.Length - 1))
                {
                    stringBuilder.AppendFormat("GPU: <{1}ms", frame, ms);
                }
                else
                {
                    stringBuilder.AppendFormat("GPU: {1}ms", frame, ms);
                }

                gpuFrameRateStrings[i] = ToCharArray(stringBuilder);

                milisecondStringBuilder.Length = 0;
                stringBuilder.Length = 0;
            }
        }

        private void BuildCharacterUVs()
        {
            characterScale = new Vector3(fontCharacterSize.x * fontScale.x, fontCharacterSize.y * fontScale.y, 1.0f);

            if (material != null && material.mainTexture != null)
            {
                for (char c = ' '; c < characterUVs.Length; ++c)
                {
                    int index = c - ' ';
                    float height = (float)fontCharacterSize.y / material.mainTexture.height;
                    float x = ((float)(index % fontColumns) * fontCharacterSize.x) / material.mainTexture.width;
                    float y = ((float)(index / fontColumns) * fontCharacterSize.y) / material.mainTexture.height;
                    characterUVs[c] = new Vector4(x, 1.0f - height - y, 0.0f, 0.0f);
                }
            }
        }

        private Vector3 CalculateWindowPosition(Transform cameraTransform)
        {
            float windowDistance = Mathf.Max(16.0f / Camera.main.fieldOfView, Camera.main.nearClipPlane + 0.25f);
            Vector3 position = cameraTransform.position + (cameraTransform.forward * windowDistance);
            Vector3 horizontalOffset = cameraTransform.right * windowOffset.x;
            Vector3 verticalOffset = cameraTransform.up * windowOffset.y;

            switch (windowAnchor)
            {
                case TextAnchor.UpperLeft: position += verticalOffset - horizontalOffset; break;
                case TextAnchor.UpperCenter: position += verticalOffset; break;
                case TextAnchor.UpperRight: position += verticalOffset + horizontalOffset; break;
                case TextAnchor.MiddleLeft: position -= horizontalOffset; break;
                case TextAnchor.MiddleRight: position += horizontalOffset; break;
                case TextAnchor.LowerLeft: position -= verticalOffset + horizontalOffset; break;
                case TextAnchor.LowerCenter: position -= verticalOffset; break;
                case TextAnchor.LowerRight: position -= verticalOffset - horizontalOffset; break;
            }

            return position;
        }

        private Quaternion CalculateWindowRotation(Transform cameraTransform)
        {
            Quaternion rotation = cameraTransform.rotation;

            switch (windowAnchor)
            {
                case TextAnchor.UpperLeft: rotation *= windowHorizontalRotationInverse * windowVerticalRotationInverse; break;
                case TextAnchor.UpperCenter: rotation *= windowHorizontalRotationInverse; break;
                case TextAnchor.UpperRight: rotation *= windowHorizontalRotationInverse * windowVerticalRotation; break;
                case TextAnchor.MiddleLeft: rotation *= windowVerticalRotationInverse; break;
                case TextAnchor.MiddleRight: rotation *= windowVerticalRotation; break;
                case TextAnchor.LowerLeft: rotation *= windowHorizontalRotation * windowVerticalRotationInverse; break;
                case TextAnchor.LowerCenter: rotation *= windowHorizontalRotation; break;
                case TextAnchor.LowerRight: rotation *= windowHorizontalRotation * windowVerticalRotation; break;
            }

            return rotation;
        }

        void LayoutText(TextData data)
        {
            Vector4 colorVector = Color.white;
            Vector4 spaceUV = characterUVs[' '];

            Vector3 position = data.Position;
            position -= Vector3.up * characterScale.y * 0.5f;
            position += (data.RightAligned) ? Vector3.right * -characterScale.x * 0.5f : Vector3.right * characterScale.x * 0.5f;

            for (int i = 0; i < maxStringLength; ++i)
            {
                instanceMatrices[data.Offset + i] = Matrix4x4.TRS(position, Quaternion.identity, characterScale);
                instanceUVOffsetScaleX[data.Offset + i] = spaceUV;
                instanceColors[data.Offset + i] = colorVector;
                position += (data.RightAligned) ? Vector3.right * -characterScale.x : Vector3.right * characterScale.x;
            }

            data.LastProcessed = maxStringLength;
            instanceColorsDirty = true;
            instanceUVOffsetScaleXDirty = true;
        }

        void SetText(TextData data, char[] text, int count, Color color)
        {
            Vector4 colorVector = color;
            Vector4 spaceUV = characterUVs[' '];

            // Only loop though characters we need to update.
            int charactersToProcess = Mathf.Min(Mathf.Max(count, data.LastProcessed), maxStringLength);

            for (int i = 0; i < charactersToProcess; ++i)
            {
                int charIndex = (data.RightAligned) ? count - i - 1 : i;
                instanceUVOffsetScaleX[data.Offset + i] = (i < count) ? characterUVs[text[charIndex]] : spaceUV;
                instanceColors[data.Offset + i] = colorVector;
            }

            data.LastProcessed = charactersToProcess;
            instanceColorsDirty = true;
            instanceUVOffsetScaleXDirty = true;
        }

#if UNITY_STANDALONE_WIN || UNITY_WSA
        private KeywordRecognizer keywordRecognizer;

        private void BuildKeywordRecognizer()
        {
            if (toggleKeyworlds.Length != 0)
            {
                keywordRecognizer = new KeywordRecognizer(toggleKeyworlds);
                keywordRecognizer.OnPhraseRecognized += OnPhraseRecognized;

                keywordRecognizer.Start();
            }
        }

        private void OnPhraseRecognized(PhraseRecognizedEventArgs args)
        {
            IsVisible = !IsVisible;
        }
#endif

        private void MemoryUsageToString(char[] buffer, int displayedDecimalDigits, TextData data, ulong memoryUsage, Color color)
        {
            bool usingGigabytes = false;
            float usage = ConvertBytesToMegabytes(memoryUsage);

            if (usage > 1024.0f)
            {
                usage = ConvertMegabytesToGigabytes(usage);
                usingGigabytes = true;
            }

            int bufferIndex = 0;

            for (int i = 0; i < data.Prefix.Length; ++i)
            {
                buffer[bufferIndex++] = data.Prefix[i];
            }

            bufferIndex = FtoA(usage, displayedDecimalDigits, buffer, bufferIndex);

            buffer[bufferIndex++] = usingGigabytes ? 'G' : 'M';
            buffer[bufferIndex++] = 'B';

            SetText(data, buffer, bufferIndex, color);
        }

        private void SceneStatsToString(char[] buffer, TextData data, long drawCalls)
        {
            int bufferIndex = 0;

            for (int i = 0; i < data.Prefix.Length; ++i)
            {
                buffer[bufferIndex++] = data.Prefix[i];
            }

            if (drawCalls > 1000)
            {
                float count = drawCalls / 1000.0f;
                bufferIndex = FtoA(count, displayedDecimalDigits, buffer, bufferIndex);
                buffer[bufferIndex++] = 'k';
            }
            else
            {
                bufferIndex = ItoA((int)drawCalls, buffer, bufferIndex);
            }

            SetText(data, buffer, bufferIndex, Color.white);
        }

        private void MeshStatsToString(char[] buffer, int displayedDecimalDigits, TextData data, long count)
        {
            int bufferIndex = 0;

            for (int i = 0; i < data.Prefix.Length; ++i)
            {
                buffer[bufferIndex++] = data.Prefix[i];
            }

            bool usingMillions = false;
            float total = count / 1000.0f;

            if (total > 1000.0f)
            {
                total /= 1000.0f;
                usingMillions = true;
            }

            bufferIndex = FtoA(total, displayedDecimalDigits, buffer, bufferIndex);

            buffer[bufferIndex++] = usingMillions ? 'm' : 'k';

            SetText(data, buffer, bufferIndex, Color.white);
        }

        private void MillisecondsToString(char[] buffer, int displayedDecimalDigits, TextData data, float milliseconds, Color color)
        {
            int bufferIndex = 0;

            for (int i = 0; i < data.Prefix.Length; ++i)
            {
                buffer[bufferIndex++] = data.Prefix[i];
            }

            bufferIndex = FtoA(milliseconds, displayedDecimalDigits, buffer, bufferIndex);

            buffer[bufferIndex++] = 'm';
            buffer[bufferIndex++] = 's';

            SetText(data, buffer, bufferIndex, color);
        }

        private static char[] ToCharArray(StringBuilder stringBuilder)
        {
            char[] output = new char[stringBuilder.Length];

            for (int i = 0; i < output.Length; ++i)
            {
                output[i] = stringBuilder[i];
            }

            return output;
        }

        private static int ItoA(int value, char[] buffer, int bufferIndex)
        {
            // Using a custom number to string method to avoid the overhead, and allocations, of built in string.Format/StringBuilder methods.
            // We can also make some assumptions since the domain of the input number is known.

            if (value == 0)
            {
                buffer[bufferIndex++] = '0';
            }
            else
            {
                int startIndex = bufferIndex;

                for (; value != 0; value /= 10)
                {
                    buffer[bufferIndex++] = (char)((char)(value % 10) + '0');
                }

                char temp;
                for (int endIndex = bufferIndex - 1; startIndex < endIndex; ++startIndex, --endIndex)
                {
                    temp = buffer[startIndex];
                    buffer[startIndex] = buffer[endIndex];
                    buffer[endIndex] = temp;
                }
            }

            return bufferIndex;
        }

        private static int FtoA(float value, int displayedDecimalDigits, char[] buffer, int bufferIndex)
        {
            int integerDigits = (int)value;
            int fractionalDigits = (int)((value - integerDigits) * Mathf.Pow(10.0f, displayedDecimalDigits));

            bufferIndex = ItoA(integerDigits, buffer, bufferIndex);

            if (displayedDecimalDigits != 0)
            {
                buffer[bufferIndex++] = '.';
            }

            if (fractionalDigits != 0)
            {
                bufferIndex = ItoA(fractionalDigits, buffer, bufferIndex);
            }
            else
            {
                for (int i = 0; i < displayedDecimalDigits; ++i)
                {
                    buffer[bufferIndex++] = '0';
                }
            }

            return bufferIndex;
        }

        private static ulong AppMemoryUsage
        {
            get
            {
#if WINDOWS_UWP
                return MemoryManager.AppMemoryUsage;
#else
                return (ulong)Profiler.GetTotalAllocatedMemoryLong();
#endif
            }
        }

        private static ulong AppMemoryUsageLimit
        {
            get
            {
#if WINDOWS_UWP
                return MemoryManager.AppMemoryUsageLimit;
#else
                return ConvertMegabytesToBytes(SystemInfo.systemMemorySize);
#endif
            }
        }

        private static bool WillDisplayedMillisecondsDiffer(float oldMilliseconds, float newMilliseconds, int displayedDecimalDigits)
        {
            float decimalPower = Mathf.Pow(10.0f, displayedDecimalDigits);

            return (int)(oldMilliseconds * decimalPower) != (int)(newMilliseconds * decimalPower);
        }

        private static bool WillDisplayedMeshStatsCountDiffer(long oldCount, long newCount, int displayedDecimalDigits)
        {
            float decimalPower = Mathf.Pow(10.0f, displayedDecimalDigits) / 1000.0f;

            return (int)(oldCount * decimalPower) != (int)(newCount * decimalPower);
        }

        private static bool WillDisplayedMemoryUsageDiffer(ulong oldUsage, ulong newUsage, int displayedDecimalDigits)
        {
            float oldUsageMBs = ConvertBytesToMegabytes(oldUsage);
            float newUsageMBs = ConvertBytesToMegabytes(newUsage);
            float decimalPower = Mathf.Pow(10.0f, displayedDecimalDigits);

            return (int)(oldUsageMBs * decimalPower) != (int)(newUsageMBs * decimalPower);
        }

        private static ulong ConvertMegabytesToBytes(int megabytes)
        {
            return ((ulong)megabytes * 1024UL) * 1024UL;
        }

        private static float ConvertBytesToMegabytes(ulong bytes)
        {
            return (bytes / 1024.0f) / 1024.0f;
        }

        private static float ConvertMegabytesToGigabytes(float megabytes)
        {
            return (megabytes / 1024.0f);
        }
    }
}

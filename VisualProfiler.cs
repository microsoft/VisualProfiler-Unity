// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using UnityEngine;
using UnityEngine.Windows.Speech;
using System.Text;
using System.Diagnostics;

#if UNITY_5_5_OR_NEWER
using UnityEngine.Profiling;
#endif

namespace Microsoft.MixedReality.Profiling
{
    /// <summary>
    /// ABOUT: The VisualProfiler provides a drop in, single file, solution for viewing 
    /// your Windows Mixed Reality Unity application's frame rate and memory usage. Missed 
    /// frames are displayed over time to visually find problem areas. Memory is reported 
    /// as current, peak and max usage in a bar graph. 
    /// 
    /// USAGE: To use this profiler simply add this script as a component of any gameobject in 
    /// your Unity scene. The profiler is initially disabled (toggle-able via the initiallyActive 
    /// property), but can be toggled via the enabled/disable voice commands keywords.
    ///
    /// IMPORTANT: Please make sure to add the microphone capability to your app if you plan 
    /// on using the enable/disable keywords, in Unity under Edit -> Project Settings -> 
    /// Player -> Settings for Windows Store -> Publishing Settings -> Capabilities or in your 
    /// Visual Studio Package.appxmanifest capabilities.
    /// </summary>
    public class VisualProfiler : MonoBehaviour
    {
        private const int maxTargetFrameRate = 120;

        [Header("Profiler Settings")]
        [SerializeField]
        private bool initiallyActive = false;
        [SerializeField]
        private string[] toggleKeyworlds = new string[]
        {
            "Profiler",
            "Toggle Profiler",
            "Show Profiler",
            "Hide Profiler"
        };
        [SerializeField]
        [Range(1, 60)]
        private int frameRange = 30;
        [SerializeField]
        [Range(0.0f, 1.0f)]
        private float frameSampleRate = 0.1f;

        [Header("UI Settings")]
        [SerializeField]
        [Range(0.0f, 100.0f)]
        private float windowFollowSpeed = 5.0f;
        [SerializeField]
        [Range(0.0f, 360.0f)]
        private float windowYawRotation = 20.0f;
        [SerializeField]
        private string usedMemoryString = "Used: {0}MB";
        [SerializeField]
        private string peakMemoryString = "Peak: {0}MB";
        [SerializeField]
        private string limitMemoryString = "Limit: {0}MB";

        [Header("UI Colors Settings")]
        [SerializeField]
        private Color baseColor = new Color(80 / 256.0f, 80 / 256.0f, 80 / 256.0f, 1.0f);
        [SerializeField]
        private Color targetFrameRateColor = new Color(127 / 256.0f, 186 / 256.0f, 0 / 256.0f, 1.0f);
        [SerializeField]
        private Color missedFrameRateColor = new Color(242 / 256.0f, 80 / 256.0f, 34 / 256.0f, 1.0f);
        [SerializeField]
        private Color memoryUsedColor = new Color(0 / 256.0f, 164 / 256.0f, 239 / 256.0f, 1.0f);
        [SerializeField]
        private Color memoryPeakColor = new Color(255 / 256.0f, 185 / 256.0f, 0 / 256.0f, 1.0f);
        [SerializeField]
        private Color memoryLimitColor = new Color(150 / 256.0f, 150 / 256.0f, 150 / 256.0f, 1.0f);

        private GameObject window;
        private TextMesh frameRateText;
        private TextMesh usedMemoryText;
        private TextMesh peakMemoryText;
        private TextMesh limitMemoryText;
        private Transform usedAnchor;
        private Transform peakAnchor;
        private Quaternion windowRotation;

        private class FrameInfo
        {
            public MaterialPropertyBlock PropertyBlock;
            public Renderer Renderer;
        }

        private FrameInfo[] frameInfo;
        private int frameOffset;
        private int frameCount;
        private Stopwatch stopwatch = new Stopwatch();
        private string[] frameRateStrings;

        private ulong memoryUsage;
        private ulong peakMemoryUsage;
        private ulong limitMemoryUsage;

        private MaterialPropertyBlock propertyBlockFrameTarget;
        private MaterialPropertyBlock propertyBlockFrameMissed;

        private KeywordRecognizer keywordRecognizer;

        private StringBuilder stringBuilder = new StringBuilder(32);

        [SerializeField]
        [HideInInspector]
        private Material defaultMaterial;
        private Material backgroundMaterial;
        private Material foregroundMaterial;
        private Material textMaterial;

        private void Reset()
        {
            if (!defaultMaterial)
            {
                defaultMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
                defaultMaterial.SetFloat("_ZWrite", 0.0f);
                defaultMaterial.SetFloat("_ZTest", 0.0f);
                defaultMaterial.renderQueue = 5000;
            }

            if (Application.isPlaying)
            {
                backgroundMaterial = new Material(defaultMaterial);
                foregroundMaterial = new Material(defaultMaterial);
                defaultMaterial.renderQueue = foregroundMaterial.renderQueue - 1;
                backgroundMaterial.renderQueue = defaultMaterial.renderQueue - 1;

                MeshRenderer meshRenderer = new GameObject().AddComponent<TextMesh>().GetComponent<MeshRenderer>();
                textMaterial = new Material(meshRenderer.sharedMaterial);
                textMaterial.renderQueue = defaultMaterial.renderQueue;
                Destroy(meshRenderer.gameObject);
            }

            stopwatch.Restart();
        }

        private void Start()
        {
            Reset();
            BuildWindow();
            BuildFrameRateStrings();
            BuildKeywordRecognizer();
        }

        private void OnDestroy()
        {
            if (keywordRecognizer.IsRunning)
            {
                keywordRecognizer.Stop();
            }

            Destroy(window);
        }

        private void LateUpdate()
        {
            if (window == null)
            {
                return;
            }

            // Update window position.
            Transform cameraTransform = Camera.main ? Camera.main.transform : null;

            if (window.activeSelf && cameraTransform != null)
            {
                float windowDistance = Mathf.Max(16.0f / Camera.main.fieldOfView, Camera.main.nearClipPlane + 0.2f);
                Vector3 position = cameraTransform.position + (cameraTransform.forward * windowDistance);
                position -= cameraTransform.up * 0.1f;
                Quaternion rotation = cameraTransform.rotation * windowRotation;

                float t = Time.deltaTime * windowFollowSpeed;
                window.transform.position = Vector3.Lerp(window.transform.position, position, t);
                window.transform.rotation = Quaternion.Slerp(window.transform.rotation, rotation, t);
            }

            ++frameCount;
            float elapsedSeconds = stopwatch.ElapsedMilliseconds * 0.001f;

            if (elapsedSeconds >= frameSampleRate)
            {
                // Update frame rate text.
                int frameRate = (int)(1.0f / (elapsedSeconds / frameCount));
                frameRateText.text = frameRateStrings[Mathf.Clamp(frameRate, 0, maxTargetFrameRate)];

                // Update frame colors.
                frameInfo[frameOffset].PropertyBlock = FrameRateToPropertyBlock(frameRate);

                for (int i = 0; i < frameRange; ++i)
                {
                    int index = (frameOffset + frameRange - i) % frameRange;
                    frameInfo[i].Renderer.SetPropertyBlock(frameInfo[index].PropertyBlock);
                }

                frameOffset = (frameOffset + 1) % frameRange;
                frameCount = 0;
                stopwatch.Restart();
            }

            // Update memory statistics.
            ulong limit = AppMemoryUsageLimit;

            if (limit != limitMemoryUsage)
            {
                limitMemoryUsage = limit;

                if (window.activeSelf)
                {
                    MemoryUsageToString(stringBuilder, limitMemoryText, limitMemoryString, limitMemoryUsage);
                }
            }

            ulong usage = AppMemoryUsage;

            if (usage != memoryUsage)
            {
                memoryUsage = usage;
                usedAnchor.localScale = new Vector3((float)memoryUsage / limitMemoryUsage, usedAnchor.localScale.y, usedAnchor.localScale.z);

                if (window.activeSelf)
                {
                    MemoryUsageToString(stringBuilder, usedMemoryText, usedMemoryString, memoryUsage);
                }
            }

            if (memoryUsage > peakMemoryUsage)
            {
                peakMemoryUsage = memoryUsage;
                peakAnchor.localScale = new Vector3((float)peakMemoryUsage / limitMemoryUsage, peakAnchor.localScale.y, peakAnchor.localScale.z);

                if (window.activeSelf)
                {
                    MemoryUsageToString(stringBuilder, peakMemoryText, peakMemoryString, peakMemoryUsage);
                }
            }
        }

        private void BuildWindow()
        {
            // Initialize property block state.
            int colorID = Shader.PropertyToID("_Color");
            propertyBlockFrameTarget = new MaterialPropertyBlock();
            propertyBlockFrameTarget.SetColor(colorID, targetFrameRateColor);
            propertyBlockFrameMissed = new MaterialPropertyBlock();
            propertyBlockFrameMissed.SetColor(colorID, missedFrameRateColor);

            // Build the window root.
            {
                window = CreateQuad("VisualProfiler", null);
                InitializeRenderer(window, backgroundMaterial, colorID, baseColor);
                window.transform.localScale = new Vector3(0.2f, 0.04f, 1.0f);
                windowRotation = Quaternion.AngleAxis(windowYawRotation, Vector3.right);
            }

            // Add frame rate text and frame indicators.
            {
                frameRateText = CreateText("FrameRateText", new Vector3(-0.495f, 0.5f, 0.0f), window.transform, TextAnchor.UpperLeft, textMaterial, Color.white, string.Empty);

                frameInfo = new FrameInfo[frameRange];
                Vector3 scale = new Vector3(1.0f / frameRange, 0.2f, 1.0f);
                Vector3 position = new Vector3(0.5f - (scale.x * 0.5f), 0.15f, 0.0f);

                for (int i = 0; i < frameRange; ++i)
                {
                    frameInfo[i] = new FrameInfo();
                    frameInfo[i].PropertyBlock = propertyBlockFrameTarget;

                    GameObject quad = CreateQuad("Frame", window.transform);
                    frameInfo[i].Renderer = InitializeRenderer(quad, defaultMaterial, colorID, missedFrameRateColor);

                    quad.transform.localPosition = position;
                    Vector3 bufferedScale = new Vector3(scale.x * 0.8f, scale.y, scale.z);
                    quad.transform.localScale = bufferedScale;

                    position.x -= scale.x;
                }
            }

            // Add memory usage text and bars.
            {
                usedMemoryText = CreateText("UsedMemoryText", new Vector3(-0.495f, 0.0f, 0.0f), window.transform, TextAnchor.UpperLeft, textMaterial, memoryUsedColor, usedMemoryString);
                peakMemoryText = CreateText("PeakMemoryText", new Vector3(0.0f, 0.0f, 0.0f), window.transform, TextAnchor.UpperCenter, textMaterial, memoryPeakColor, peakMemoryString);
                limitMemoryText = CreateText("LimitMemoryText", new Vector3(0.495f, 0.0f, 0.0f), window.transform, TextAnchor.UpperRight, textMaterial, Color.white, limitMemoryString);

                GameObject limitBar = CreateQuad("LimitBar", window.transform);
                InitializeRenderer(limitBar, defaultMaterial, colorID, memoryLimitColor);
                limitBar.transform.localScale = new Vector3(0.99f, 0.2f, 1.0f);
                limitBar.transform.localPosition = new Vector3(0.0f, -0.37f, 0.0f);

                {
                    usedAnchor = CreateAnchor("UsedAnchor", limitBar.transform);
                    GameObject bar = CreateQuad("UsedBar", usedAnchor);
                    Material material = new Material(foregroundMaterial);
                    material.renderQueue = material.renderQueue + 1;
                    InitializeRenderer(bar, material, colorID, memoryUsedColor);
                    bar.transform.localScale = Vector3.one;
                    bar.transform.localPosition = new Vector3(0.5f, 0.0f, 0.0f);
                }
                {
                    peakAnchor = CreateAnchor("PeakAnchor", limitBar.transform);
                    GameObject bar = CreateQuad("PeakBar", peakAnchor);
                    InitializeRenderer(bar, foregroundMaterial, colorID, memoryPeakColor);
                    bar.transform.localScale = Vector3.one;
                    bar.transform.localPosition = new Vector3(0.5f, 0.0f, 0.0f);
                }
            }

            window.SetActive(initiallyActive);
        }

        private void BuildFrameRateStrings()
        {
            frameRateStrings = new string[maxTargetFrameRate + 1];

            StringBuilder milisecondStringBuilder = new StringBuilder(16);
            stringBuilder.Clear();

            for (int i = 0; i < frameRateStrings.Length; ++i)
            {
                float miliseconds = (i == 0) ? 0.0f : (1.0f / i) * 1000.0f;
                milisecondStringBuilder.AppendFormat("{0:F1}", miliseconds);
                stringBuilder.AppendFormat("{0} fps ({1} ms)", i.ToString(), milisecondStringBuilder.ToString());
                frameRateStrings[i] = stringBuilder.ToString();
                milisecondStringBuilder.Clear();
                stringBuilder.Clear();
            }
        }

        private void BuildKeywordRecognizer()
        {
            keywordRecognizer = new KeywordRecognizer(toggleKeyworlds);
            keywordRecognizer.OnPhraseRecognized += OnPhraseRecognized;

            keywordRecognizer.Start();
        }

        private void OnPhraseRecognized(PhraseRecognizedEventArgs args)
        {
            if (window != null)
            {
                if (window.activeSelf)
                {
                    window.SetActive(false);
                }
                else
                {
                    window.SetActive(true);

                    // Force refresh of strings.
                    MemoryUsageToString(stringBuilder, limitMemoryText, limitMemoryString, limitMemoryUsage);
                    MemoryUsageToString(stringBuilder, usedMemoryText, usedMemoryString, memoryUsage);
                    MemoryUsageToString(stringBuilder, peakMemoryText, peakMemoryString, peakMemoryUsage);
                }
            }
        }

        private MaterialPropertyBlock FrameRateToPropertyBlock(int frameRate)
        {
            // Application frame rate minus one because the integer frame rate is rounded down.
            return (frameRate >= ((int)(AppFrameRate) - 1)) ? propertyBlockFrameTarget : propertyBlockFrameMissed;
        }

        private static Transform CreateAnchor(string name, Transform parent)
        {
            Transform anchor = new GameObject(name).transform;
            anchor.parent = parent;
            anchor.localScale = Vector3.one;
            anchor.localPosition = new Vector3(-0.5f, 0.0f, 0.0f);

            return anchor;
        }

        private static GameObject CreateQuad(string name, Transform parent)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(quad.GetComponent<Collider>());
            quad.name = name;
            quad.transform.parent = parent;

            return quad;
        }

        private static TextMesh CreateText(string name, Vector3 position, Transform parent, TextAnchor anchor, Material material, Color color, string text)
        {
            GameObject obj = new GameObject(name);
            obj.transform.localScale = Vector3.one * 0.0016f;
            obj.transform.parent = parent;
            obj.transform.localPosition = position;
            TextMesh textMesh = obj.AddComponent<TextMesh>();
            textMesh.fontSize = 48;
            textMesh.anchor = anchor;
            textMesh.color = color;
            textMesh.text = text;

            Renderer renderer = obj.GetComponent<Renderer>();
            renderer.sharedMaterial = material;

            OptimizeRenderer(renderer);

            return textMesh;
        }

        private static Renderer InitializeRenderer(GameObject obj, Material material, int colorID, Color color)
        {
            Renderer renderer = obj.GetComponent<Renderer>();
            renderer.sharedMaterial = material;

            MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(colorID, color);
            renderer.SetPropertyBlock(propertyBlock);

            OptimizeRenderer(renderer);

            return renderer;
        }

        private static void OptimizeRenderer(Renderer renderer)
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
#if UNITY_5_5_OR_NEWER
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
#else
            renderer.motionVectors = false;
#endif
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
#if UNITY_2017_2_OR_NEWER
            renderer.allowOcclusionWhenDynamic = false;
#endif
        }

        private static void MemoryUsageToString(StringBuilder stringBuilder, TextMesh textMesh, string memoryString, ulong memoryUsage)
        {
            stringBuilder.Clear();
            // $TODO(thmicka): something faster than float to string?
            string megabytes = stringBuilder.AppendFormat("{0:F1}", ConvertBytesToMegabytes(memoryUsage)).ToString();
            stringBuilder.Clear();
            textMesh.text = stringBuilder.AppendFormat(memoryString, megabytes).ToString();
        }

        private static float AppFrameRate
        {
            get
            {
                float refreshRate;
#if UNITY_2017_2_OR_NEWER
                refreshRate = UnityEngine.XR.XRDevice.refreshRate;
#else
                refreshRate = UnityEngine.VR.XRDevice.refreshRate;
#endif
                // If the current XR SDK does not report refresh rate information, assume 60Hz.
                return ((int)refreshRate == 0) ? 60.0f : refreshRate;
            }
        }

        private static ulong AppMemoryUsage
        {
            get
            {
#if WINDOWS_UWP
                return Windows.System.MemoryManager.AppMemoryUsage;
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
                return Windows.System.MemoryManager.AppMemoryUsageLimit;
#else
                return ConvertMegabytesToBytes(SystemInfo.systemMemorySize);
#endif
            }
        }

        private static ulong ConvertMegabytesToBytes(int megabytes)
        {
            return ((ulong)megabytes * 1024UL) * 1024UL;
        }

        private static float ConvertBytesToMegabytes(ulong bytes)
        {
            return (bytes / 1024.0f) / 1024.0f;
        }
    }
}

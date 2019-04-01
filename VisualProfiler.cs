// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using UnityEngine;
using UnityEngine.Windows.Speech;
using System.Text;
using UnityEngine.Profiling;

namespace Microsoft.MixedReality.Profiling
{
    /// <summary>
    /// ABOUT: The VisualProfiler provides a drop in, single file, solution for viewing 
    /// your Windows Mixed Reality Unity application's frame rate and memory usage. Missed 
    /// frames are displayed over time to visually find problem areas. Memory is reported 
    /// as current, peak and max usage in a bar graph. 
    /// 
    /// USAGE: To use this profiler simply add this script as a component of any gameobject in 
    /// your Unity scene. The profiler is initially enabled (toggle-able via the initiallyActive 
    /// property), but can be toggled via the enabled/disable voice commands keywords.
    ///
    /// IMPORTANT: Please make sure to add the microphone capability to your app if you plan 
    /// on using the enable/disable keywords, in Unity under Edit -> Project Settings -> 
    /// Player -> Settings for Windows Store -> Publishing Settings -> Capabilities or in your 
    /// Visual Studio Package.appxmanifest capabilities.
    /// </summary>
    public class VisualProfiler : MonoBehaviour
    {
        [Header("Profiler Settings")]
        [SerializeField]
        private bool initiallyActive = true;
        [SerializeField]
        private string[] toggleKeyworlds = new string[] { "Profiler", "Toggle Profiler", "Show Profiler", "Hide Profiler" };
        [SerializeField, Range(1, 60)]
        private int frameRange = 30;
        [SerializeField, Range(0.0f, 1.0f)]
        private float frameSampleRate = 0.1f;

        [Header("Window Settings")]
        [SerializeField]
        private TextAnchor windowAnchor = TextAnchor.LowerCenter;
        [SerializeField]
        private Vector2 windowOffset = new Vector2(0.075f, 0.1f);
        [SerializeField, Range(0.5f, 5.0f)]
        private float windowScale = 1.0f;
        [SerializeField, Range(0.0f, 100.0f)]
        private float windowFollowSpeed = 5.0f;

        [Header("UI Settings")]
        [SerializeField, Range(0, 3)]
        private int displayedDecimalDigits = 1;
        [SerializeField]
        private string usedMemoryString = "Used: {0}MB";
        [SerializeField]
        private string peakMemoryString = "Peak: {0}MB";
        [SerializeField]
        private string limitMemoryString = "Limit: {0}MB";
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
        private Quaternion windowHorizontalRotation;
        private Quaternion windowHorizontalRotationInverse;
        private Quaternion windowVerticalRotation;
        private Quaternion windowVerticalRotationInverse;

        private Matrix4x4[] frameInfoMatricies;
        private Vector4[] frameInfoColors;
        private MaterialPropertyBlock frameInfoPropertyBlock;
        private int colorID;
        private int parentMatrixID;
        private int frameCount;
        private System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        private string[] frameRateStrings;
        private string displayedDecimalFormat;

        private ulong memoryUsage;
        private ulong peakMemoryUsage;
        private ulong limitMemoryUsage;

        private KeywordRecognizer keywordRecognizer;

        private StringBuilder stringBuilder = new StringBuilder(32);

        // Rendering resources.
        [SerializeField, HideInInspector]
        private Material defaultMaterial;
        [SerializeField, HideInInspector]
        private Material defaultInstancedMaterial;
        private Material backgroundMaterial;
        private Material foregroundMaterial;
        private Material textMaterial;
        private Mesh quadMesh;

        private static readonly int maxTargetFrameRate = 120;
        private static readonly Vector2 defaultWindowRotation = new Vector2(10.0f, 20.0f);
        private static readonly Vector3 defaultWindowScale = new Vector3(0.2f, 0.04f, 1.0f);

        private void Reset()
        {
            if (defaultMaterial == null)
            {
                defaultMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
                defaultMaterial.SetFloat("_ZWrite", 0.0f);
                defaultMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Disabled);
                defaultMaterial.renderQueue = 5000;
            }

            if (defaultInstancedMaterial == null)
            {
                Shader defaultInstancedShader = Shader.Find("Hidden/Instanced-Colored");

                if (defaultInstancedShader != null)
                {
                    defaultInstancedMaterial = new Material(defaultInstancedShader);
                    defaultInstancedMaterial.enableInstancing = true;
                    defaultInstancedMaterial.SetFloat("_ZWrite", 0.0f);
                    defaultInstancedMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Disabled);
                    defaultInstancedMaterial.renderQueue = 5000;
                }
                else
                {
                    Debug.LogWarning("A shader supporting instancing could not be found for the VisualProfiler, falling back to traditional rendering.");
                }
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

                MeshFilter quadMeshFilter = GameObject.CreatePrimitive(PrimitiveType.Quad).GetComponent<MeshFilter>();
                if (defaultInstancedMaterial != null)
                {
                    // Create a quad mesh with artificially large bounds to disable culling for instanced rendering.
                    // TODO: Use shared mesh with normal bounds once Unity allows for more control over instance culling.
                    quadMesh = quadMeshFilter.mesh;
                    quadMesh.bounds = new Bounds(Vector3.zero, Vector3.one * float.MaxValue);
                }
                else
                {
                    quadMesh = quadMeshFilter.sharedMesh;
                }
                Destroy(quadMeshFilter.gameObject);
            }

            stopwatch.Reset();
            stopwatch.Start();
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

            // Update window transformation.
            Transform cameraTransform = Camera.main ? Camera.main.transform : null;

            if (window.activeSelf && cameraTransform != null)
            {
                float t = Time.deltaTime * windowFollowSpeed;
                window.transform.position = Vector3.Lerp(window.transform.position, CalculateWindowPosition(cameraTransform), t);
                window.transform.rotation = Quaternion.Slerp(window.transform.rotation, CalculateWindowRotation(cameraTransform), t);
                window.transform.localScale = defaultWindowScale * windowScale;
            }

            ++frameCount;
            float elapsedSeconds = stopwatch.ElapsedMilliseconds * 0.001f;

            if (elapsedSeconds >= frameSampleRate)
            {
                // Update frame rate text.
                int frameRate = (int)(1.0f / (elapsedSeconds / frameCount));
                frameRateText.text = frameRateStrings[Mathf.Clamp(frameRate, 0, maxTargetFrameRate)];

                // Update frame colors.
                for (int i = frameRange - 1; i > 0; --i)
                {
                    frameInfoColors[i] = frameInfoColors[i - 1];
                }

                frameInfoColors[0] = (frameRate >= ((int)(AppFrameRate) - 1)) ? targetFrameRateColor : missedFrameRateColor;
                frameInfoPropertyBlock.SetVectorArray(colorID, frameInfoColors);

                // Reset timers.
                frameCount = 0;
                stopwatch.Reset();
                stopwatch.Start();
            }

            // Draw frame info.
            if (window.activeSelf)
            {
                Matrix4x4 parentLocalToWorldMatrix = window.transform.localToWorldMatrix;

                if (defaultInstancedMaterial != null)
                {
                    frameInfoPropertyBlock.SetMatrix(parentMatrixID, parentLocalToWorldMatrix);
                    Graphics.DrawMeshInstanced(quadMesh, 0, defaultInstancedMaterial, frameInfoMatricies, frameInfoMatricies.Length, frameInfoPropertyBlock, UnityEngine.Rendering.ShadowCastingMode.Off, false);
                }
                else
                {
                    // If a instanced material is not available, fall back to non-instanced rendering.
                    for (int i  = 0; i < frameInfoMatricies.Length; ++i)
                    {
                        frameInfoPropertyBlock.SetColor(colorID, frameInfoColors[i]);
                        Graphics.DrawMesh(quadMesh, parentLocalToWorldMatrix * frameInfoMatricies[i], defaultMaterial, 0, null, 0, frameInfoPropertyBlock, false, false, false);
                    }
                }
            }

            // Update memory statistics.
            ulong limit = AppMemoryUsageLimit;

            if (limit != limitMemoryUsage)
            {
                if (window.activeSelf && WillDisplayedMemoryUsageDiffer(limitMemoryUsage, limit, displayedDecimalDigits))
                {
                    MemoryUsageToString(stringBuilder, displayedDecimalFormat, limitMemoryText, limitMemoryString, limit);
                }

                limitMemoryUsage = limit;
            }

            ulong usage = AppMemoryUsage;

            if (usage != memoryUsage)
            {
                usedAnchor.localScale = new Vector3((float)usage / limitMemoryUsage, usedAnchor.localScale.y, usedAnchor.localScale.z);

                if (window.activeSelf && WillDisplayedMemoryUsageDiffer(memoryUsage, usage, displayedDecimalDigits))
                {
                    MemoryUsageToString(stringBuilder, displayedDecimalFormat, usedMemoryText, usedMemoryString, usage);
                }

                memoryUsage = usage;
            }

            if (memoryUsage > peakMemoryUsage)
            {
                peakAnchor.localScale = new Vector3((float)memoryUsage / limitMemoryUsage, peakAnchor.localScale.y, peakAnchor.localScale.z);

                if (window.activeSelf && WillDisplayedMemoryUsageDiffer(peakMemoryUsage, memoryUsage, displayedDecimalDigits))
                {
                    MemoryUsageToString(stringBuilder, displayedDecimalFormat, peakMemoryText, peakMemoryString, memoryUsage);
                }

                peakMemoryUsage = memoryUsage;
            }
        }

        private Vector3 CalculateWindowPosition(Transform cameraTransform)
        {
            float windowDistance = Mathf.Max(16.0f / Camera.main.fieldOfView, Camera.main.nearClipPlane + 0.2f);
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

        private void BuildWindow()
        {
            // Initialize property block state.
            colorID = Shader.PropertyToID("_Color");
            parentMatrixID = Shader.PropertyToID("_ParentLocalToWorldMatrix");

            // Build the window root.
            {
                window = CreateQuad("VisualProfiler", null);
                InitializeRenderer(window, backgroundMaterial, colorID, baseColor);
                window.transform.localScale = defaultWindowScale;
                windowHorizontalRotation = Quaternion.AngleAxis(defaultWindowRotation.y, Vector3.right);
                windowHorizontalRotationInverse = Quaternion.Inverse(windowHorizontalRotation);
                windowVerticalRotation = Quaternion.AngleAxis(defaultWindowRotation.x, Vector3.up);
                windowVerticalRotationInverse = Quaternion.Inverse(windowVerticalRotation);
            }

            // Add frame rate text and frame indicators.
            {
                frameRateText = CreateText("FrameRateText", new Vector3(-0.495f, 0.5f, 0.0f), window.transform, TextAnchor.UpperLeft, textMaterial, Color.white, string.Empty);

                frameInfoMatricies = new Matrix4x4[frameRange];
                frameInfoColors = new Vector4[frameRange];
                Vector3 scale = new Vector3(1.0f / frameRange, 0.2f, 1.0f);
                Vector3 position = new Vector3(0.5f - (scale.x * 0.5f), 0.15f, 0.0f);

                for (int i = 0; i < frameRange; ++i)
                {
                    frameInfoMatricies[i] = Matrix4x4.TRS(position, Quaternion.identity, new Vector3(scale.x * 0.8f, scale.y, scale.z));
                    position.x -= scale.x;
                    frameInfoColors[i] = targetFrameRateColor;
                }

                frameInfoPropertyBlock = new MaterialPropertyBlock();
                frameInfoPropertyBlock.SetVectorArray(colorID, frameInfoColors);
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
            displayedDecimalFormat = string.Format("{{0:F{0}}}", displayedDecimalDigits);

            StringBuilder milisecondStringBuilder = new StringBuilder(16);
            stringBuilder.Length = 0;

            for (int i = 0; i < frameRateStrings.Length; ++i)
            {
                float miliseconds = (i == 0) ? 0.0f : (1.0f / i) * 1000.0f;
                milisecondStringBuilder.AppendFormat(displayedDecimalFormat, miliseconds);
                stringBuilder.AppendFormat("{0} fps ({1} ms)", i.ToString(), milisecondStringBuilder.ToString());
                frameRateStrings[i] = stringBuilder.ToString();
                milisecondStringBuilder.Length = 0;
                stringBuilder.Length = 0;
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
                    MemoryUsageToString(stringBuilder, displayedDecimalFormat, limitMemoryText, limitMemoryString, limitMemoryUsage);
                    MemoryUsageToString(stringBuilder, displayedDecimalFormat, usedMemoryText, usedMemoryString, memoryUsage);
                    MemoryUsageToString(stringBuilder, displayedDecimalFormat, peakMemoryText, peakMemoryString, peakMemoryUsage);
                }
            }
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
            textMesh.richText = false;

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
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
#if UNITY_2017_2_OR_NEWER
            renderer.allowOcclusionWhenDynamic = false;
#endif
        }

        private static void MemoryUsageToString(StringBuilder stringBuilder, string displayedDecimalFormat, TextMesh textMesh, string memoryString, ulong memoryUsage)
        {
            stringBuilder.Length = 0;
            // Note, this can trigger an allocation and can be called each frame. But, with default settings this function should only fire with memory deltas of +-1KB.
            string megabytes = stringBuilder.AppendFormat(displayedDecimalFormat, ConvertBytesToMegabytes(memoryUsage)).ToString();
            stringBuilder.Length = 0;
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

        private static bool WillDisplayedMemoryUsageDiffer(ulong oldUsage, ulong newUsage, int memoryUsageDecimalDigits)
        {
            float oldUsageMBs = ConvertBytesToMegabytes(oldUsage);
            float newUsageMBs = ConvertBytesToMegabytes(newUsage);

            for (int i = 0; i < memoryUsageDecimalDigits; ++i)
            {
                oldUsageMBs *= 10.0f;
                newUsageMBs *= 10.0f;
            }

            return (int)oldUsageMBs != (int)newUsageMBs;
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

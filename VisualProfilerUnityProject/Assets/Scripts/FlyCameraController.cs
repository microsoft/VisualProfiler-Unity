// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if GT_USE_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
#define USE_INPUT_SYSTEM
#endif

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

#if USE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif // USE_INPUT_SYSTEM

namespace Microsoft.MixedReality.GraphicsTools
{
    /// <summary>
    /// A simple "fly" camera for moving the camera while playing with a mouse/keyboard or gamepad.
    /// </summary>
    public class FlyCameraController : MonoBehaviour
    {
        private class CameraState
        {
            public Vector3 Position;
            public Vector3 Rotation;

            public void SetFromTransform(Transform t)
            {
                Position = t.position;
                Vector3 euler = t.eulerAngles;
                Rotation = new Vector3(euler.y, euler.x, euler.z);
            }

            public void Translate(Vector3 translation)
            {
                Vector3 rotatedTranslation = Quaternion.Euler(new Vector3(Rotation.y, Rotation.x, Rotation.z)) * translation;
                Position += rotatedTranslation;
            }

            public void LerpTowards(CameraState target, float positionLerp, float rotationLerp)
            {
                Rotation = Vector3.Lerp(Rotation, target.Rotation, rotationLerp);
                Position = Vector3.Lerp(Position, target.Position, positionLerp);
            }

            public void UpdateTransform(Transform t)
            {
                t.eulerAngles = new Vector3(Rotation.y, Rotation.x, Rotation.z);
                t.position = Position;
            }
        }

        [Header("Movement Settings")]
        [Tooltip("Exponential boost factor on translation, controllable by mouse wheel.")]
        public float Boost = 1.0f;

        [Tooltip("Minimum boost factor applied to translation.")]
        public float MinBoost = 0.5f;

        [Tooltip("Maximum boost factor applied to translation.")]
        public float MaxBoost = 4.0f;

        [Tooltip("Time it takes to interpolate camera position 99% of the way to the target."), Range(0.001f, 1.0f)]
        public float PositionLerpTime = 0.2f;

        [Header("Rotation Settings")]
        [Tooltip("X = Change in mouse position.\nY = Multiplicative factor for camera rotation.")]
        public AnimationCurve MouseSensitivityCurve = new AnimationCurve(new Keyframe(0.0f, 0.5f, 0.0f, 5f), new Keyframe(1.0f, 2.5f, 0.0f, 0.0f));

        [Tooltip("Time it takes to interpolate camera rotation 99% of the way to the target."), Range(0.001f, 1.0f)]
        public float RotationLerpTime = 0.1f;

        [Tooltip("Whether or not to invert our Y axis for mouse/gamepad input to rotation.")]
        public bool InvertY = false;

        [Header("Other Settings")]
        public bool showControlsText = false;

        private CameraState targetCameraState = new CameraState();
        private CameraState interpolatingCameraState = new CameraState();
        private List<XRDisplaySubsystem> xrDisplaySubsystems = new List<XRDisplaySubsystem>();

        /// <summary>
        /// Called when the game object state from from inactive to active.
        /// </summary>
        private void OnEnable()
        {
            targetCameraState.SetFromTransform(transform);
            interpolatingCameraState.SetFromTransform(transform);
        }

        /// <summary>
        /// Sets up initial state.
        /// </summary>
        private void Start()
        {
            SubsystemManager.GetInstances(xrDisplaySubsystems);
        }

        /// <summary>
        /// Called every frame to poll input and update the camera transform.
        /// </summary>
        private void Update()
        {
            if (!XRDeviceIsPresent() && Application.isFocused)
            {
                // Lock cursor when right mouse button pressed.
#if USE_INPUT_SYSTEM
                if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
#else
                if (Input.GetMouseButtonDown(1))
#endif // USE_INPUT_SYSTEM
                {
                    Cursor.lockState = CursorLockMode.Locked;
#if UNITY_EDITOR
                    Cursor.visible = false;
#endif
                }

                // Unlock when right mouse button released.
#if USE_INPUT_SYSTEM
                if (Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame)
#else
                if (Input.GetMouseButtonUp(1))
#endif // USE_INPUT_SYSTEM
                {
                    Cursor.lockState = CursorLockMode.None;
#if UNITY_EDITOR
                    Cursor.visible = true;
#endif
                }

                // Rotation.
                Vector2 rotation = GetInputRotationDirection();
                float rotationSensitivityFactor = MouseSensitivityCurve.Evaluate(rotation.magnitude);
                targetCameraState.Rotation.x += rotation.x * rotationSensitivityFactor;
                targetCameraState.Rotation.y += rotation.y * rotationSensitivityFactor;

                // Translation.
                float dt = Time.deltaTime;
                Vector3 translation = GetInputTranslationDirection() * dt;

                // Speed up movement when shift key held.
#if USE_INPUT_SYSTEM
                if (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed)
#else
                if (Input.GetKey(KeyCode.LeftShift))
#endif // USE_INPUT_SYSTEM
                {
                    translation *= 10.0f;
                }

                // Modify movement by a boost factor (defined in Inspector and modified in play mode through the mouse scroll wheel).
#if USE_INPUT_SYSTEM
                Vector2 scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue() : Vector2.zero;
                Boost += scroll.y * dt;
#else
                Boost += Input.mouseScrollDelta.y * dt;
#endif // USE_INPUT_SYSTEM
                Boost = Mathf.Clamp(Boost, MinBoost, MaxBoost);

                translation *= Mathf.Pow(2.0f, Boost);

                targetCameraState.Translate(translation);

                // Framerate-independent interpolation.
                // Calculate the lerp amount, such that we get 99% of the way to our target in the specified time.
                float positionLerp = 1.0f - Mathf.Exp((Mathf.Log(1.0f - 0.99f) / PositionLerpTime) * dt);
                float rotationLerp = 1.0f - Mathf.Exp((Mathf.Log(1.0f - 0.99f) / RotationLerpTime) * dt);
                interpolatingCameraState.LerpTowards(targetCameraState, positionLerp, rotationLerp);

                interpolatingCameraState.UpdateTransform(transform);
            }
        }

        /// <summary>
        /// Displays the camera controls via a user interface.
        /// </summary>
        private void OnGUI()
        {
            if (!XRDeviceIsPresent() && showControlsText)
            {
#if USE_INPUT_SYSTEM
                bool gamepadPresent = Gamepad.current != null;
#else
                bool gamepadPresent = false;
#endif // USE_INPUT_SYSTEM

                if (gamepadPresent)
                {
                    GUI.Label(new Rect(10.0f, 10.0f, 256.0f, 128.0f), "Camera Controls\nRight Stick to Rotate\nLeft Stick to Translate");
                }
                else
                {
                    GUI.Label(new Rect(10.0f, 10.0f, 256.0f, 128.0f), "Camera Controls\nRight Click + Mouse Move to Rotate\n'W' 'A' 'S' 'D' to Translate");
                }
            }
        }

        /// <summary>
        /// Returns true if an XR device is connected and running. For example a VR headset.
        /// </summary>
        private bool XRDeviceIsPresent()
        {
            foreach (var xrDisplay in xrDisplaySubsystems)
            {
                if (xrDisplay != null && xrDisplay.running)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Turns mouse/stick controls into an input vector.
        /// </summary>
        private Vector2 GetInputRotationDirection()
        {
            Vector2 direction = Vector3.zero;

#if USE_INPUT_SYSTEM
            if (Mouse.current != null && Mouse.current.rightButton.isPressed)
#else
            if (Input.GetMouseButton(1))
#endif // USE_INPUT_SYSTEM
            {
#if USE_INPUT_SYSTEM
                // Magical value to feel like the editor.
                const float rotationScaler = 0.075f;
                Vector2 delta = Mouse.current.delta.ReadValue() * rotationScaler; 
                direction += new Vector2(delta.x, delta.y * (InvertY ? 1.0f : -1.0f));
#else
                direction += new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y") * (InvertY ? 1.0f : -1.0f));
#endif // USE_INPUT_SYSTEM
            }

#if USE_INPUT_SYSTEM
            if (Gamepad.current != null)
            {
                // Magical value to feel like the editor.
#if UNITY_GAMECORE && !UNITY_EDITOR
                const float rotationScaler = 1.0f;
#else
                const float rotationScaler = 0.25f;
#endif
                Vector2 delta = Gamepad.current.rightStick.ReadValue() * rotationScaler;
                direction += new Vector2(delta.x, delta.y * (InvertY ? 1.0f : -1.0f));
            }
#else
            // TODO - [Cameron-Micka] is is possible to query the right stick without setting up virtual axes in the legacy input system?
#endif // USE_INPUT_SYSTEM

            return direction;
        }

        /// <summary>
        /// Turns WASD/stick controls into an input vector.
        /// </summary>
        private Vector3 GetInputTranslationDirection()
        {
            Vector3 direction = Vector3.zero;

#if USE_INPUT_SYSTEM
            if (Keyboard.current != null)
            {
                if (Keyboard.current.wKey.isPressed) { direction += Vector3.forward; }
                if (Keyboard.current.sKey.isPressed) { direction += Vector3.back; }
                if (Keyboard.current.aKey.isPressed) { direction += Vector3.left; }
                if (Keyboard.current.dKey.isPressed) { direction += Vector3.right; }
                if (Keyboard.current.qKey.isPressed) { direction += Vector3.down; }
                if (Keyboard.current.eKey.isPressed) { direction += Vector3.up; }
                direction.Normalize();
            }

            if (Gamepad.current != null)
            {
                Vector2 stick = Gamepad.current.leftStick.ReadValue();
                direction += new Vector3(stick.x, 0.0f, stick.y);
            }
#else
            if (Input.GetKey(KeyCode.W)) { direction += Vector3.forward; }
            if (Input.GetKey(KeyCode.S)) { direction += Vector3.back; }
            if (Input.GetKey(KeyCode.A)) { direction += Vector3.left; }
            if (Input.GetKey(KeyCode.D)) { direction += Vector3.right; }
            if (Input.GetKey(KeyCode.Q)) { direction += Vector3.down; }
            if (Input.GetKey(KeyCode.E)) { direction += Vector3.up; }
            direction.Normalize();

            direction += new Vector3(Input.GetAxis("Horizontal"), 0.0f, Input.GetAxis("Vertical"));

#endif // USE_INPUT_SYSTEM

            return direction;
        }
    }
}

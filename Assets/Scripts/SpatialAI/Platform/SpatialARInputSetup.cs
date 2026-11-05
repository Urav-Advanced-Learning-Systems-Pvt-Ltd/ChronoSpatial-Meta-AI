using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARFoundation;

namespace SpatialAI
{
    public static class SpatialARInputSetup
    {
        public const string PositionPath = "<HandheldARInputDevice>/devicePosition";
        public const string RotationPath = "<HandheldARInputDevice>/deviceRotation";

        public static void RepairLegacyCameraBindings(Camera camera)
        {
            if (camera == null) return;
            var driver = camera.GetComponent<TrackedPoseDriver>();
            if (driver == null) return;
            var position = driver.positionInput;
            if (position.reference == null && position.action != null && position.action.bindings.Count == 1
                && position.action.bindings[0].path == "<XRHMD>/centerEyePosition")
            {
                driver.positionInput = new InputActionProperty(new InputAction("Device Position", InputActionType.Value, PositionPath));
                position.action.Dispose();
            }
            var rotation = driver.rotationInput;
            if (rotation.reference == null && rotation.action != null && rotation.action.bindings.Count == 1
                && rotation.action.bindings[0].path == "<XRHMD>/centerEyeRotation")
            {
                driver.rotationInput = new InputActionProperty(new InputAction("Device Rotation", InputActionType.Value, RotationPath));
                rotation.action.Dispose();
            }
        }

        public static ARInputManager EnsureInputManager(ARSession session)
        {
            var existing = Object.FindAnyObjectByType<ARInputManager>();
            if (existing != null || session == null) return existing;
            return session.gameObject.AddComponent<ARInputManager>();
        }
    }
}

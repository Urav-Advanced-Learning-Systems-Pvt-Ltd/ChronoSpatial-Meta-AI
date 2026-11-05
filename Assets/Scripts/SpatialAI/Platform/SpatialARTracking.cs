using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARSubsystems;

namespace SpatialAI
{
    public static class SpatialARTracking
    {
        public static bool TryGetPoseValidity(Camera camera, out string status)
        {
            var valid = TryReadCameraPose(camera, out _, out status);
            return ARSession.state == ARSessionState.SessionTracking && valid;
        }

        public static bool TryReadCameraPose(Camera camera, out SpatialPose pose, out string status)
        {
            pose = default;
            var driver = camera == null ? null : camera.GetComponent<TrackedPoseDriver>();
            var positionAction = driver == null ? null : driver.positionInput.action;
            var rotationAction = driver == null ? null : driver.rotationInput.action;
            status = "HandheldInput: camera pose driver/actions unavailable or disabled";
            if (driver == null || !driver.isActiveAndEnabled || positionAction == null || rotationAction == null
                || !positionAction.enabled || !rotationAction.enabled) return false;
            HandheldARInputDevice device = null;
            foreach (var positionControl in positionAction.controls)
            {
                if (!(positionControl.device is HandheldARInputDevice candidate) || positionControl != candidate.devicePosition) continue;
                foreach (var rotationControl in rotationAction.controls)
                {
                    if (rotationControl != candidate.deviceRotation) continue;
                    if (device != null && device != candidate) { status = "HandheldInput: ambiguous camera pose devices"; return false; }
                    device = candidate;
                }
            }
            if (device == null) { status = "HandheldInput: position/rotation controls do not resolve to the same handheld device"; return false; }
            pose = new SpatialPose(device.devicePosition.ReadValue(), device.deviceRotation.ReadValue(), Time.realtimeSinceStartup);
            var stateControl = device.TryGetChildControl<IntegerControl>("trackingState");
            var tracking = stateControl == null ? 0 : (InputTrackingState)stateControl.ReadValue();
            status = $"HandheldInput={device.name}#{device.deviceId} added={device.added} enabled={device.enabled} controls={positionAction.controls.Count}/{rotationAction.controls.Count} nativePosition={pose.position:F4} nativeRotation={pose.rotation:F4} trackingState={(stateControl==null?"N/A":tracking.ToString())} validitySource=ARSession+BoundHandheldPose";
            return ValidatePoseEvidence(true, device.added && device.enabled, stateControl != null, tracking, true, true, pose);
        }

        // Handheld AR does not require the legacy XR trackingState feature. ARSession's
        // SessionTracking state comes from the provider's tracking state, not pose motion.
        // Still require real native pose values, and honor explicit XR flags when available.
        public static bool ValidatePoseEvidence(bool sessionTracking, bool deviceValid, bool hasState,
            InputTrackingState tracking, bool hasPosition, bool hasRotation, SpatialPose pose)
        {
            var required = InputTrackingState.Position | InputTrackingState.Rotation;
            return sessionTracking && deviceValid && hasPosition && hasRotation && SpatialFrameGeometry.ValidPose(pose)
                && (!hasState || (tracking & required) == required);
        }
    }
}

using System;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialAI
{
    public sealed class ARFoundationFrameProvider : MonoBehaviour, ISpatialFrameProvider
    {
        public ARCameraManager CameraManager;
        public Camera Camera;
        public int ImageFramesAcquired { get; private set; }
        public int ImageConversionFailures { get; private set; }
        public int ImageFramesConverted { get; private set; }
        public int AcquisitionAttempts { get; private set; }
        public int ConversionAttempts { get; private set; }
        public string ImageGeometry { get; private set; } = "N/A";
        public string LastStage { get; private set; } = "Not sampled";
        public string LastFailure { get; private set; } = "Not sampled";
        public bool ImageAcquisitionSupported => CameraManager != null && CameraManager.subsystem != null && CameraManager.subsystem.running;
        double previousImageTimestamp = double.NegativeInfinity;
        double nextAcquisition;

        public bool TryAcquireFrame(out SpatialFrame frame, out SpatialFailureReason failureReason, out string message)
        {
            frame = null;
            failureReason = SpatialFailureReason.None;
            message = "";
            LastStage = "ResolveCamera";
            var origin = GetComponentInParent<XROrigin>();
            if (origin != null) Camera = origin.Camera;
            CameraManager = Camera == null ? null : Camera.GetComponent<ARCameraManager>();
            if (Camera == null || CameraManager == null || !Camera.isActiveAndEnabled)
                return Reject(SpatialFailureReason.CameraImageUnavailable, "Active XROrigin camera unavailable", out failureReason, out message);
            LastStage = "SessionTracking";
            if (ARSession.state != ARSessionState.SessionTracking)
                return Reject(SpatialFailureReason.TrackingUnavailable, "AR session not tracking", out failureReason, out message);
            LastStage = "NativePoseTracking";
            if (!SpatialARTracking.TryGetPoseValidity(Camera, out var trackingStatus))
                return Reject(SpatialFailureReason.TrackingUnavailable, "Camera pose input unavailable: " + trackingStatus, out failureReason, out message);
            LastStage = "PoseDriverBindings";
            var poseDriver = Camera.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            if (poseDriver != null && (!poseDriver.isActiveAndEnabled || poseDriver.positionInput.action == null || poseDriver.rotationInput.action == null
                || poseDriver.positionInput.action.controls.Count == 0 || poseDriver.rotationInput.action.controls.Count == 0))
                return Reject(SpatialFailureReason.TrackingUnavailable, "TrackedPoseDriver has no resolved position/rotation controls", out failureReason, out message);
            var now = Time.realtimeSinceStartupAsDouble;
            LastStage = "AcquisitionThrottle";
            if (now < nextAcquisition)
                return Reject(SpatialFailureReason.NoFrame, "Acquisition throttled", out failureReason, out message);
            nextAcquisition = now + .15;
            LastStage = "AcquireCpuImage";
            AcquisitionAttempts++;
            if (!CameraManager.TryAcquireLatestCpuImage(out var image))
                return Reject(SpatialFailureReason.CameraImageUnavailable, "CPU image unavailable", out failureReason, out message);
            ImageFramesAcquired++;
            Texture2D texture = null;
            try
            {
                LastStage = "ImageTimestamp";
                if (!SpatialFrameGeometry.Finite(image.timestamp) || image.timestamp <= previousImageTimestamp)
                    return Reject(SpatialFailureReason.InvalidFrame, "Duplicate or regressed image timestamp; reset frame history after session reset", out failureReason, out message);
                previousImageTimestamp = image.timestamp;
                LastStage = "SamplePose";
                if (!ARCoreImagePose.TryAcquire(CameraManager, Camera, image.timestamp, (float)now, out var imagePose, out var pose, out var poseEvidence))
                    return Reject(SpatialFailureReason.InvalidFrame, poseEvidence, out failureReason, out message);
                LastStage = "ConversionFormat";
                if (!image.FormatSupported(TextureFormat.RGBA32))
                    return Reject(SpatialFailureReason.CameraImageFormatUnsupported, "RGBA32 CPU conversion unsupported", out failureReason, out message);
                var dimensions = image.dimensions;
                if (dimensions.x <= 0 || dimensions.y <= 0)
                    return Reject(SpatialFailureReason.InvalidFrame, "Invalid image dimensions", out failureReason, out message);
                var scale = Mathf.Min(1f, 640f / Mathf.Max(dimensions.x, dimensions.y));
                var output = new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(dimensions.x * scale)), Mathf.Max(1, Mathf.RoundToInt(dimensions.y * scale)));
                var conversion = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32, XRCpuImage.Transformation.None) { outputDimensions = output };
                ImageGeometry = $"source={image.format}:{dimensions.x}x{dimensions.y} converted=RGBA32:{output.x}x{output.y} transform=None {poseEvidence}";
                LastStage = "ConvertImage";
                ConversionAttempts++;
                using (var converted = new NativeArray<byte>(image.GetConvertedDataSize(conversion), Allocator.Temp))
                {
                    image.Convert(conversion, converted);
                    texture = new Texture2D(output.x, output.y, TextureFormat.RGBA32, false);
                    texture.LoadRawTextureData(converted);
                    texture.Apply(false, false);
                }
                ImageFramesConverted++;
                LastStage = "ReadIntrinsics";
                var intrinsics = default(SpatialCameraIntrinsics);
                if (CameraManager.TryGetIntrinsics(out var native))
                    intrinsics = SpatialFrameGeometry.Scale(new SpatialCameraIntrinsics { fx = native.focalLength.x, fy = native.focalLength.y,
                        cx = native.principalPoint.x, cy = native.principalPoint.y, imageWidth = native.resolution.x, imageHeight = native.resolution.y }, output.x, output.y);
                frame = new SpatialFrame { image = texture, hasImage = true, arPose = pose, imagePose = imagePose, hasImagePose = true, imagePoseEvidence = poseEvidence, trackingValid = true,
                    imageTimestamp = image.timestamp, receivedTimestamp = now, clockDomain = "XRCpuImage-seconds",
                    cameraIdentity = Camera.name + "#" + Camera.GetEntityId(), source = ARCoreImagePose.Source,
                    intrinsics = intrinsics, intrinsicsReason = intrinsics.IsValid ? "Available; scaled to converted image" : "Native camera intrinsics unavailable" };
                texture = null;
                LastFailure = "None";
                LastStage = "FrameReady";
                return true;
            }
            catch (Exception exception)
            {
                ImageConversionFailures++;
                return Reject(SpatialFailureReason.InvalidFrame, "CPU conversion failed: " + exception.Message, out failureReason, out message);
            }
            finally
            {
                image.Dispose();
                if (texture != null) Destroy(texture);
            }
        }

        bool Reject(SpatialFailureReason reason, string detail, out SpatialFailureReason failureReason, out string message)
        {
            failureReason = reason;
            message = detail;
            LastFailure = detail;
            return false;
        }
        public void ResetFrameHistory(bool resetCounters = false)
        {
            previousImageTimestamp = double.NegativeInfinity; nextAcquisition = 0;
            if (resetCounters) { ImageFramesAcquired = 0; ImageConversionFailures = 0; ImageFramesConverted = 0; AcquisitionAttempts = 0; ConversionAttempts = 0; ImageGeometry = "N/A"; }
        }
    }
}

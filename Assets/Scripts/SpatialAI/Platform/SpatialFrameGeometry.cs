using System;
using UnityEngine;

namespace SpatialAI
{
    public static class SpatialFrameGeometry
    {
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static bool ValidPose(SpatialPose pose)
        {
            var rotation = pose.rotation;
            var norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return Finite(pose.position.x) && Finite(pose.position.y) && Finite(pose.position.z)
                && Finite(norm) && Math.Abs(norm - 1) < .01 && Finite(pose.timestamp);
        }
        public static SpatialCameraIntrinsics Scale(SpatialCameraIntrinsics source, int width, int height)
        {
            if (!source.IsValid || width <= 0 || height <= 0) return default;
            var horizontal = width / (float)source.imageWidth;
            var vertical = height / (float)source.imageHeight;
            return new SpatialCameraIntrinsics { fx = source.fx * horizontal, fy = source.fy * vertical,
                cx = source.cx * horizontal, cy = source.cy * vertical, imageWidth = width, imageHeight = height };
        }
        public static bool Fresh(SpatialFrame frame, double now, out string reason)
        {
            reason = "";
            if (frame == null || !frame.trackingValid || !ValidPose(frame.arPose)) reason = "tracking or pose invalid";
            else if (frame.source == ARCoreImagePose.LegacySource) reason = ARCoreImagePose.IncompatibleMessage;
            else if ((frame.source == ARCoreImagePose.Source && !frame.hasImagePose) || (frame.hasImagePose && !ValidPose(frame.imagePose))) reason = "physical image pose unavailable or invalid";
            else if (!Finite(frame.imageTimestamp) || frame.imageTimestamp < 0) reason = "image timestamp unavailable";
            else if (string.IsNullOrEmpty(frame.clockDomain)) reason = "image clock domain unavailable";
            else if (!Finite(frame.receivedTimestamp) || !Finite(now) || frame.receivedTimestamp > now || now - frame.receivedTimestamp > .5) reason = "stale frame";
            return reason.Length == 0;
        }
    }
}

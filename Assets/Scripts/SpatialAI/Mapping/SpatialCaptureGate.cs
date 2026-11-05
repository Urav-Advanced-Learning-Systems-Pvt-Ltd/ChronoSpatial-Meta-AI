using UnityEngine;

namespace SpatialAI
{
    public sealed class SpatialCaptureGate
    {
        SpatialPose? visualPose;
        SpatialPose? poseOnlyPose;
        double visualTime;
        double poseOnlyTime;
        double lastImageTimestamp = double.NegativeInfinity;
        string imageClock;

        public SpatialPose? PreviousVisualPose => visualPose;
        // Preview a candidate without consuming its pose/time until worker validation succeeds.
        public SpatialCaptureGate Copy() => (SpatialCaptureGate)MemberwiseClone();
        public int VisualCount { get; private set; }
        public int PoseOnlyCount { get; private set; }

        public bool AcceptVisual(SpatialFrame frame, double now, int maximum, float interval,
            float translation, float rotation, out string reason)
        {
            if (!SpatialFrameGeometry.Fresh(frame, now, out reason)) return false;
            if (string.IsNullOrEmpty(frame.clockDomain)) { reason = "image clock domain unavailable"; return false; }
            if (imageClock != null && imageClock != frame.clockDomain) { reason = "image clock changed; start a new capture"; return false; }
            if (frame.imageTimestamp <= lastImageTimestamp) { reason = "duplicate or regressed image timestamp"; return false; }
            imageClock = frame.clockDomain;
            lastImageTimestamp = frame.imageTimestamp;
            if (!Evaluate(frame.arPose, visualPose, frame.receivedTimestamp, visualTime, VisualCount + PoseOnlyCount,
                maximum, interval, translation, rotation, out reason)) return false;
            visualPose = frame.arPose;
            visualTime = frame.receivedTimestamp;
            VisualCount++;
            return true;
        }

        public bool AcceptPose(SpatialPose pose, bool trackingValid, double now, int maximum,
            float interval, float translation, float rotation, out string reason)
        {
            if (!trackingValid) { reason = "tracking invalid"; return false; }
            if (!Evaluate(pose, poseOnlyPose, now, poseOnlyTime, VisualCount + PoseOnlyCount,
                maximum, interval, translation, rotation, out reason)) return false;
            poseOnlyPose = pose;
            poseOnlyTime = now;
            PoseOnlyCount++;
            return true;
        }

        static bool Evaluate(SpatialPose pose, SpatialPose? previous, double now, double last,
            int count, int maximum, float interval, float translation, float rotation, out string reason)
        {
            reason = "accepted";
            if (!SpatialFrameGeometry.ValidPose(pose) || !SpatialFrameGeometry.Finite(now)) reason = "invalid pose or timestamp";
            else if (count >= maximum) reason = "maximum keyframe count reached";
            else if (previous.HasValue && now <= last) reason = "timestamp not advanced";
            else if (previous.HasValue && now - last < interval) reason = "keyframe interval not elapsed";
            else if (previous.HasValue && Vector3.Distance(pose.position, previous.Value.position) < translation
                && Quaternion.Angle(pose.rotation, previous.Value.rotation) < rotation) reason = "movement below thresholds";
            return reason == "accepted";
        }
    }
}

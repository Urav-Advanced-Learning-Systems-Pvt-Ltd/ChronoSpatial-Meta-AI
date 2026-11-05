namespace SpatialAI
{
    // Native camera tracking never grants a saved-map alignment.
    public sealed class SpatialRuntimeStatus
    {
        public enum ScanPhase { Idle, Scanning, PausedNativeLost, Finalizing, Complete }
        public enum NativeStatus { Unknown, NotTracking, Limited, Tracking }
        public enum AlignmentStatus { None, CaptureOrigin, VisuallyVerified, Lost, Unverified }
        public enum LocalizationStatus { Idle, Running, Succeeded, Failed, Cancelled, Required }
        public ScanPhase Scan { get; private set; }
        public NativeStatus Native { get; private set; }
        public AlignmentStatus Alignment { get; private set; }
        public LocalizationStatus Localization { get; private set; }
        public int TrackingSegment { get; private set; }
        public string LocalizationFailure { get; private set; }
        bool interrupted;

        public void SetNative(NativeStatus value)
        {
            if (Native == value) return;
            var previous = Native; Native = value;
            if (value != NativeStatus.Tracking)
            {
                if (previous == NativeStatus.Tracking) interrupted = true;
                if (Scan == ScanPhase.Scanning) Scan = ScanPhase.PausedNativeLost;
                if (Alignment == AlignmentStatus.CaptureOrigin || Alignment == AlignmentStatus.VisuallyVerified)
                    Alignment = AlignmentStatus.Lost;
                if (Localization == LocalizationStatus.Running) Localization = LocalizationStatus.Cancelled;
                else if (Localization == LocalizationStatus.Succeeded) Localization = LocalizationStatus.Required;
            }
            else
            {
                if (interrupted) { TrackingSegment++; interrupted = false; }
                if (Scan == ScanPhase.PausedNativeLost) Scan = ScanPhase.Scanning;
                if (Alignment == AlignmentStatus.Lost) Alignment = AlignmentStatus.Unverified;
            }
        }
        public void BeginScan()
        {
            Scan = Native == NativeStatus.Tracking ? ScanPhase.Scanning : ScanPhase.PausedNativeLost;
            Alignment = Native == NativeStatus.Tracking ? AlignmentStatus.CaptureOrigin : AlignmentStatus.None;
            Localization = LocalizationStatus.Idle; LocalizationFailure = null;
        }
        public void Stop(bool pending)
        {
            if (Scan == ScanPhase.Scanning || Scan == ScanPhase.PausedNativeLost)
                Scan = pending ? ScanPhase.Finalizing : ScanPhase.Complete;
        }
        public void Drain(bool idle) { if (Scan == ScanPhase.Finalizing && idle) Scan = ScanPhase.Complete; }
        public void Loaded() { Scan = ScanPhase.Idle; Alignment = AlignmentStatus.None; Localization = LocalizationStatus.Idle; LocalizationFailure = null; }
        public void BeginLocalization() { Localization = LocalizationStatus.Running; LocalizationFailure = null; Alignment = AlignmentStatus.None; }
        public void EndLocalization(bool success, string failure)
        {
            Localization = success ? LocalizationStatus.Succeeded : LocalizationStatus.Failed;
            LocalizationFailure = success ? null : failure;
            if (success && Native == NativeStatus.Tracking) Alignment = AlignmentStatus.VisuallyVerified;
        }
        public void InvalidateAlignment()
        {
            if (Alignment == AlignmentStatus.CaptureOrigin || Alignment == AlignmentStatus.VisuallyVerified) Alignment = AlignmentStatus.Unverified;
            if (Localization == LocalizationStatus.Succeeded) Localization = LocalizationStatus.Required;
        }
    }

    public sealed class SpatialCaptureHint
    {
        string rejection;
        double expires, skippingSince = -1;
        bool busy;
        public void Clear() { rejection = null; skippingSince = -1; busy = false; }
        public void Note(string decision, double now)
        {
            if (decision.StartsWith("Accepted") || decision.StartsWith("Queued")) { Clear(); return; }
            if (decision.StartsWith("Skipped:") || decision.StartsWith("Deferred:"))
            {
                busy = decision.StartsWith("Deferred:");
                if (skippingSince < 0) skippingSince = now;
                return;
            }
            rejection = decision; expires = now + 2.5; skippingSince = -1;
        }
        public string Text(double now, bool scanning)
        {
            if (!scanning) return "";
            if (rejection != null && now < expires) return rejection;
            if (busy) return "Processing the previous frame";
            return skippingSince >= 0 && now - skippingSince >= 3 ? "Move or turn to capture a new keyframe" : "";
        }
    }
}

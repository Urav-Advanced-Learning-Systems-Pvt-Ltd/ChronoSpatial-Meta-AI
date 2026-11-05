namespace SpatialAI
{
    public interface ISpatialFrameProvider
    {
        bool ImageAcquisitionSupported { get; }
        bool TryAcquireFrame(out SpatialFrame frame, out SpatialFailureReason failureReason, out string message);
    }
}

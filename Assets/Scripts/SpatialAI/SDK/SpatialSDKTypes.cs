using System;
using UnityEngine;

namespace SpatialAI
{
    public enum SpatialMapReadiness { PoseOnly, FeatureBearing, LandmarkBearing, LocalizationReady }
    public enum SpatialTrackingState { Unavailable, Initializing, Searching, Localized, Tracking, TrackingLost }
    public enum SpatialFailureReason { None, NoMap, PoseOnlyMap, MapNotLocalizationReady, NoFrame, CameraImageUnavailable, CameraImageFormatUnsupported, InvalidFrame, InvalidIntrinsics, NoFeatures, NoCandidates, NoMatches, InsufficientEvidence, NoLandmarks, PoseSolverUnavailable, ReprojectionErrorTooHigh, TrackingUnavailable, UnsupportedProvider, InvalidMap, PoseEstimationFailed }

    [Serializable] public struct SpatialMapPose
    {
        public Vector3 position;
        public Quaternion rotation;
        public float timestamp;
        public float confidence;
        public int inlierCount;
        public float reprojectionError;
        public string mapId;
        public bool valid;
    }

    [Serializable] public sealed class SpatialFrame
    {
        public Texture2D image;
        public SpatialPose arPose;
        public SpatialPose imagePose;
        public bool hasImagePose;
        public string imagePoseEvidence;
        public SpatialPose ImagePose => hasImagePose ? imagePose : arPose;
        public SpatialCameraIntrinsics intrinsics;
        public bool hasImage;
        public bool trackingValid; public string intrinsicsReason;
        public string source;
        public double imageTimestamp = double.NaN;
        public double receivedTimestamp = double.NaN;
        public string clockDomain;
        public string cameraIdentity;
        public float Timestamp => arPose.timestamp;
    }

    [Serializable] public struct SpatialLocalizationDiagnostics
    {
        public int queryFeatureCount;
        public int candidateKeyframeCount;
        public int rawMatchCount;
        public bool rawMatchCountAvailable;
        public int acceptedMatchCount;
        public int landmarkBackedMatchCount;
        public int observationMatchCount, trackMatchCount, associationDuplicates, geometryRejected, invalidReferences;
        public int reciprocalMatchCount, missingAssociationCount, landmarkReferenceCount;
        public long descriptorComparisonCount;
        public int correspondenceCount;
        public int inlierCount;
        public float reprojectionError;
        public int bestHypothesisInliers;
        public string poseFailure;
    }

    [Serializable] public sealed class SpatialLocalizationResult
    {
        public bool success;
        public SpatialTrackingState state;
        public SpatialFailureReason failureReason;
        public string message;
        public string mapId;
        public float confidence;
        public SpatialMapPose pose;
        public SpatialLocalizationDiagnostics diagnostics;
        public static SpatialLocalizationResult Failure(SpatialTrackingState state, SpatialFailureReason reason, string message) => new SpatialLocalizationResult { state=state, failureReason=reason, message=message };
    }

    [Serializable] public sealed class SpatialDiagnostics
    {
        public string mapId; public SpatialMapReadiness mapReadiness; public string mapReason;
        public int keyframes; public int features; public int tracks; public int landmarks; public float averageTrackObservations;
        public bool frameAvailable; public int imageWidth; public int imageHeight; public bool intrinsicsAvailable; public string intrinsicsReason; public bool cameraTracking;
        public int ratioRejected; public int distanceRejected; public int invalidRejected; public int ambiguousRejected; public int duplicateRejected; public int trackExtensionAttempts; public int tracksExtended; public int newTracks; public int trackConflicts; public int observationsCreated; public int observationsRejected; public int triangulationAttempts; public int triangulationSuccesses; public int triangulationFailures; public string triangulationFailureReason;
        public SpatialLocalizationDiagnostics localization; public SpatialTrackingState trackingState; public bool mapAlignmentValid;
        public bool featureExtractionAttempted; public bool poseSolverAttempted;
        public bool mapPoseValid; public SpatialMapPose mapPose; public string failureReason;
    }

    public sealed class SpatialSDKConfiguration
    {
        public float FrameInterval = .25f;
        public int MinimumFeaturesForLocalization = 8;
        public int MaximumCandidateKeyframes = 6;
        public float TrackingPoseTimeout = 1.0f;
        public ISpatialFeatureExtractor FeatureExtractor = new LocalBinaryFeatureExtractor();
        public ISpatialFeatureMatcher FeatureMatcher = new BinaryFeatureMatcher();
        public IPoseSolver PoseSolver = new PnPPoseSolver();
    }
}

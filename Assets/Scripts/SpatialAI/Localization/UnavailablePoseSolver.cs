using System.Collections.Generic;
namespace SpatialAI { public sealed class UnavailablePoseSolver : IPoseSolver { public SpatialPoseEstimate Solve(IReadOnlyList<SpatialCorrespondence> correspondences, SpatialCameraIntrinsics intrinsics) { return new SpatialPoseEstimate{success=false,inlierCount=0,reprojectionError=float.PositiveInfinity}; } } }

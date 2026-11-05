using System;
using System.Collections.Generic;

namespace SpatialAI
{
    // Optional diagnostic sink: records existing decisions without changing acceptance.
    [Serializable] public sealed class SpatialInitializationTrace
    {
        public string inputFailure;
        public string failureReason;
        public int bestHypothesisInliers;
        public bool captureHypotheses = true;
        public List<SpatialHypothesisTrace> hypotheses = new List<SpatialHypothesisTrace>();
        public void Record(SpatialHypothesisTrace hypothesis)
        {
            bestHypothesisInliers = Math.Max(bestHypothesisInliers, hypothesis.inliers);
            if (captureHypotheses) hypotheses.Add(hypothesis);
        }
    }

    [Serializable] public sealed class SpatialHypothesisTrace
    {
        public string method = "DLT";
        public int attempt;
        public List<string> sampleIds = new List<string>();
        public List<string> reached = new List<string>();
        public string outcome;
        public float determinant, firstScale, secondScale, thirdScale, forwardAgreement;
        public int invalidDepth, reprojectionRejected, inliers;
        public bool Reject(string reason) { outcome = reason; return false; }
    }
}

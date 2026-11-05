namespace SpatialAI
{
    public static class SpatialFeatureSchemas
    {
        public const string Orb = "opencv-orb-4.12-256";
        public const string OrbDistinct = "spatial-orb-4.12-1000-nms4-v1";
        public static bool IsOrb(string schema) => schema == Orb || schema == OrbDistinct;
        public static string ForMap(SpatialMapData map)
        {
            if (map?.keyframes != null)
                foreach (var frame in map.keyframes)
                    if (frame?.features != null)
                        foreach (var feature in frame.features)
                            if (feature != null) return feature.descriptorType;
            return null;
        }
        public static string NewScan
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return OrbDistinct;
#else
                return BinaryPatchDescriptor.Type;
#endif
            }
        }
    }
}

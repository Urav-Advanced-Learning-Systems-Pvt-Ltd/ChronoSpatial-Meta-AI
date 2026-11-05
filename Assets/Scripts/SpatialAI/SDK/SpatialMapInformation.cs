namespace SpatialAI
{
    public static class SpatialMapInformation
    {
        public static string Summary(SpatialMapData map, SpatialSDKSession session)
        {
            if (map == null) return "Local map information (no language model): no map loaded.";
            var validation = SpatialMapValidationResult.Validate(map);
            if (!validation.valid) return "Local map information: map invalid. " + validation.message;
            var localized = session != null && session.TryGetMapPose(out var pose) && pose.valid;
            return "Local map information (deterministic; no language model)\n"
                + "Readiness: " + validation.readiness + "\n"
                + "Landmarks: " + validation.landmarkCount + "\n"
                + "Localization currently valid: " + localized + "\n"
                + "Saved user-named places: " + map.places.Count + "\n"
                + "No automatic object recognition or navigation.";
        }
    }
}

using System;
using UnityEngine;

namespace SpatialAI
{
    [Serializable]
    public sealed class SpatialPlace
    {
        public const int MaximumNameLength = 64;
        public string placeId;
        public string name;
        public Vector3 mapPosition;
    }

    public static class SpatialPlaceInformation
    {
        public static string Describe(SpatialPlace place, SpatialMapPose cameraPose)
        {
            if (place == null) return "No place selected.";
            if (!cameraPose.valid) return "Selected user-named place: " + place.name + ". Distance unavailable: localization invalid.";
            var offset = Quaternion.Inverse(cameraPose.rotation) * (place.mapPosition - cameraPose.position);
            var bearing = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            return "Selected user-named place: " + place.name + ". Straight-line distance: " + offset.magnitude.ToString("F2")
                + " m; camera-relative bearing: " + bearing.ToString("F1") + " degrees (positive right). Not a navigation route.";
        }
    }
}

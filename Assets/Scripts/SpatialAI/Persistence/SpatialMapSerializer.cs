using System;
using UnityEngine;

namespace SpatialAI
{
    public static class SpatialMapSerializer
    {
        public const int CurrentVersion = 2;
        public const int MaximumCharacters = 32 * 1024 * 1024;
        [Serializable] sealed class Envelope { public string schema; public int version; public SpatialMapData map; }

        public static string Serialize(SpatialMapData map)
        {
            var validation = SpatialMapValidationResult.Validate(map);
            if (!validation.valid) throw new ArgumentException(validation.message);
            var json = JsonUtility.ToJson(new Envelope { schema = "SpatialAI-native-pixels-ydown", version = CurrentVersion, map = map });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumCharacters) throw new ArgumentException("Map exceeds serialized resource limit");
            return json;
        }
        public static SpatialMapData Deserialize(string json) => Deserialize(json, out _);
        public static SpatialMapData Deserialize(string json, out string reason)
        {
            reason = "Map JSON is missing, malformed, or over resource limits";
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumCharacters) return null;
            try
            {
                var envelope = JsonUtility.FromJson<Envelope>(json);
                if (envelope == null || envelope.version != CurrentVersion || envelope.schema != "SpatialAI-native-pixels-ydown") { reason = "Unsupported map schema; preserve the map and make a fresh scan"; return null; }
                var validation = SpatialMapValidationResult.Validate(envelope.map);
                reason = validation.valid ? null : validation.message;
                return validation.valid ? envelope.map : null;
            }
            catch (ArgumentException) { return null; }
        }
    }
}

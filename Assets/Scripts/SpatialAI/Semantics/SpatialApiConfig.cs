using System;
using UnityEngine;

namespace SpatialAI
{
    // Centralized API configuration for Meta AI services. Singleton service
    // persisted in PlayerPrefs so key, endpoint, and models survive restarts.
    // Note: PlayerPrefs is plaintext on device; treat the stored key as a
    // local convenience, not a secret store.
    public sealed class SpatialApiConfig
    {
        public const string DefaultBaseEndpoint = "https://api.meta.ai/v1";
        public const string DefaultModelName = "muse-spark-1.3";
        public const string DefaultSamModelName = "sam-3.1";

        const string KeyApiKey = "SpatialAI.ApiKey";
        const string KeyBaseEndpoint = "SpatialAI.BaseEndpoint";
        const string KeyModelName = "SpatialAI.ModelName";
        const string KeySamModelName = "SpatialAI.SamModelName";

        static SpatialApiConfig instance;
        public static SpatialApiConfig Instance
        {
            get { return instance ?? (instance = Load()); }
        }

        // Test hook / scene bootstrap may replace the singleton.
        public static void SetInstance(SpatialApiConfig config) { instance = config; }

        public string ApiKey { get; private set; }
        public string BaseEndpoint { get; private set; }
        public string ModelName { get; private set; }
        public string SamModelName { get; private set; }

        public event Action Changed;

        SpatialApiConfig() { }

        public static SpatialApiConfig Load()
        {
            return new SpatialApiConfig
            {
                ApiKey = PlayerPrefs.GetString(KeyApiKey, ""),
                BaseEndpoint = PlayerPrefs.GetString(KeyBaseEndpoint, DefaultBaseEndpoint),
                ModelName = PlayerPrefs.GetString(KeyModelName, DefaultModelName),
                SamModelName = PlayerPrefs.GetString(KeySamModelName, DefaultSamModelName)
            };
        }

        public string ResponsesEndpoint
        {
            get
            {
                string baseUrl = string.IsNullOrWhiteSpace(BaseEndpoint)
                    ? DefaultBaseEndpoint : BaseEndpoint.Trim().TrimEnd('/');
                return baseUrl + "/responses";
            }
        }

        public bool HasApiKey { get { return !string.IsNullOrWhiteSpace(ApiKey); } }

        public void Set(string apiKey, string baseEndpoint, string modelName, string samModelName = null)
        {
            ApiKey = apiKey != null ? apiKey.Trim() : "";
            BaseEndpoint = string.IsNullOrWhiteSpace(baseEndpoint) ? DefaultBaseEndpoint : baseEndpoint.Trim();
            ModelName = string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName.Trim();
            if (samModelName != null)
                SamModelName = string.IsNullOrWhiteSpace(samModelName) ? DefaultSamModelName : samModelName.Trim();
            Save();
        }

        public void Save()
        {
            PlayerPrefs.SetString(KeyApiKey, ApiKey ?? "");
            PlayerPrefs.SetString(KeyBaseEndpoint, BaseEndpoint ?? DefaultBaseEndpoint);
            PlayerPrefs.SetString(KeyModelName, ModelName ?? DefaultModelName);
            PlayerPrefs.SetString(KeySamModelName, SamModelName ?? DefaultSamModelName);
            PlayerPrefs.Save();
            if (Changed != null) Changed.Invoke();
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(KeyApiKey);
            PlayerPrefs.DeleteKey(KeyBaseEndpoint);
            PlayerPrefs.DeleteKey(KeyModelName);
            PlayerPrefs.DeleteKey(KeySamModelName);
            PlayerPrefs.Save();
            instance = Load();
            if (Changed != null) Changed.Invoke();
        }
    }
}

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace SpatialAI
{
    // In-app API settings, rendered with UI Toolkit (SettingsUI.uxml +
    // SpatialUI.uss). A gear button sits top-right with a 24px margin; the
    // modal is a 60% dimmer with a centered floating frosted-charcoal card,
    // labeled Meta API Key / Base Endpoint / Reasoning Model /
    // Perception Model fields, a Show/Hide toggle sharing the key row,
    // and a blue Test & Save ping (authenticated GET {base}/models).
    // The document sorts at 400, above navigation (200) and place (150).
    public class SpatialSettingsUI : MonoBehaviour
    {
        [SerializeField] int pingTimeoutSeconds = 20;

        UIDocument document;
        VisualElement overlay;
        TextField apiKeyField, endpointField, modelField, samModelField;
        Button keyToggle, testSaveButton;
        Label statusLabel;
        bool built, testing, open;

        public bool IsOpen { get { return open; } }
        public string StatusMessage { get { return statusLabel != null ? statusLabel.text : ""; } }

        void Start() { Build(); }

        // Self-host in play mode so the gear appears without scene edits.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<SpatialSettingsUI>() != null) return;
            new GameObject("Spatial Settings UI", typeof(SpatialSettingsUI));
        }

        void Build()
        {
            if (built) return;
            built = true;
            document = SpatialUIToolkitHost.Attach(gameObject, "SettingsUI", 400);
            if (document == null) return;
            var root = document.rootVisualElement;
            overlay = root.Q<VisualElement>("overlay");
            apiKeyField = root.Q<TextField>("apiKeyField");
            endpointField = root.Q<TextField>("endpointField");
            modelField = root.Q<TextField>("modelField");
            samModelField = root.Q<TextField>("samModelField");
            statusLabel = root.Q<Label>("statusLabel");
            testSaveButton = root.Q<Button>("testSaveBtn");
            keyToggle = root.Q<Button>("keyToggle");
            if (apiKeyField != null) apiKeyField.isPasswordField = true;
            var gear = root.Q<Button>("gearBtn");
            if (gear != null) gear.clicked += TogglePanel;
            if (keyToggle != null) keyToggle.clicked += ToggleKeyVisibility;
            if (testSaveButton != null) testSaveButton.clicked += OnTestAndSave;
            var close = root.Q<Button>("closeBtn");
            if (close != null) close.clicked += ClosePanel;
            LoadFields();
            SpatialUIToolkitHost.Show(overlay, false);
            Debug.Log("[SpatialAI Settings] UI Toolkit settings attached (sortingOrder 400).");
        }

        void LoadFields()
        {
            var config = SpatialApiConfig.Instance;
            if (apiKeyField != null) apiKeyField.value = config.ApiKey ?? "";
            if (endpointField != null) endpointField.value = config.BaseEndpoint;
            if (modelField != null) modelField.value = config.ModelName;
            if (samModelField != null) samModelField.value = config.SamModelName;
        }

        public void TogglePanel()
        {
            if (document == null) Build();
            if (overlay == null) return;
            open = !open;
            Debug.Log("[SpatialAI UI] Settings panel open=" + open);
            if (open) LoadFields();
            SpatialUIToolkitHost.Show(overlay, open);
            if (open) overlay.BringToFront();
        }

        public void ClosePanel()
        {
            open = false;
            SpatialUIToolkitHost.Show(overlay, false);
        }

        void ToggleKeyVisibility()
        {
            if (apiKeyField == null) return;
            apiKeyField.isPasswordField = !apiKeyField.isPasswordField;
            if (keyToggle != null) keyToggle.text = apiKeyField.isPasswordField ? "Show" : "Hide";
        }

        void SetStatus(string message, bool success)
        {
            if (statusLabel == null) return;
            statusLabel.text = message;
            statusLabel.RemoveFromClassList(success ? "status-error" : "status-ok");
            statusLabel.AddToClassList(success ? "status-ok" : "status-error");
        }

        void OnTestAndSave()
        {
            if (testing) return;
            StartCoroutine(TestAndSave());
        }

        // Verification ping: an authenticated GET against {base}/models.
        // A POST to /responses is a method error (HTTP 405) on model
        // catalogs, so the ping must not use it.
        public static string BuildModelsUrl(string baseEndpoint)
        {
            string baseUrl = string.IsNullOrWhiteSpace(baseEndpoint)
                ? SpatialApiConfig.DefaultBaseEndpoint : baseEndpoint.Trim();
            return baseUrl.TrimEnd('/') + "/models";
        }

        IEnumerator TestAndSave()
        {
            testing = true;
            if (testSaveButton != null) testSaveButton.SetEnabled(false);
            SetStatus("Testing…", true);
            string key = apiKeyField != null ? apiKeyField.value.Trim() : "";
            string endpoint = endpointField != null ? endpointField.value.Trim() : "";
            string modelName = modelField != null ? modelField.value.Trim() : "";
            string samModelName = samModelField != null ? samModelField.value.Trim() : "";
            if (string.IsNullOrWhiteSpace(key))
            {
                SetStatus("Failed: API key is empty", false);
                testing = false;
                if (testSaveButton != null) testSaveButton.SetEnabled(true);
                yield break;
            }
            string baseUrl = string.IsNullOrWhiteSpace(endpoint)
                ? SpatialApiConfig.DefaultBaseEndpoint : endpoint.TrimEnd('/');
            string url = BuildModelsUrl(baseUrl);
            string reasoningModel = string.IsNullOrWhiteSpace(modelName)
                ? SpatialApiConfig.DefaultModelName : modelName;
            string perceptionModel = string.IsNullOrWhiteSpace(samModelName)
                ? SpatialApiConfig.DefaultSamModelName : samModelName;
            bool ok = false;
            string failure = "";
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Authorization", "Bearer " + key);
                request.timeout = Mathf.Max(1, pingTimeoutSeconds);
                yield return request.SendWebRequest();
                ok = request.result == UnityWebRequest.Result.Success &&
                     request.responseCode >= 200 && request.responseCode < 300;
                if (!ok) failure = "HTTP " + request.responseCode + ": " + request.error;
            }
            if (ok)
            {
                SpatialApiConfig.Instance.Set(key, baseUrl, reasoningModel, perceptionModel);
                SetStatus("Connected", true);
                Debug.Log("[SpatialAI Settings] API key verified and saved to PlayerPrefs.");
            }
            else
            {
                SetStatus("Failed: " + failure, false);
                Debug.LogWarning("[SpatialAI Settings] Verification ping failed: " + failure);
            }
            testing = false;
            if (testSaveButton != null) testSaveButton.SetEnabled(true);
        }
    }
}

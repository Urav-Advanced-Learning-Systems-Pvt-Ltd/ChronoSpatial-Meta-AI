using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialAI
{
    // Entry-point marking on UI Toolkit (PlaceUI.uxml + SpatialUI.uss,
    // sortingOrder 150): a slim glass bottom bar with a Royal Blue
    // "Set Entry Point" action, a center crosshair, and a floating tag
    // that tracks the 3D marker. Replaces the old IMGUI overlay.
    // The marker anchor is the origin/entry reference for the mapped space.
    [DefaultExecutionOrder(1000)]
    public sealed class SpatialPlaceUI : MonoBehaviour
    {
        public const string EntryPointPlaceName = "Main Entry";
        static readonly string[] LegacyPlaceNames = { "Main Destination", "Demo Place" };
        public string EntryPointName { get; private set; } = EntryPointPlaceName;
        SpatialAIController controller;
        // Entry marking removed: the Set Entry Point button never cleared its
        // tracking/placement gates on device, so the button is gone and the
        // bar below keeps only the single-line status message. Saved entries
        // still resolve and render through FindPlace and the marker tag.

        Transform contentRoot;
        GameObject marker;
        Material markerMaterial;
        string message = "Scan an area to populate the map.";
        UIDocument document;
        VisualElement barRoot;
        Label messageLabel, markerTag;
        SpatialSettingsUI settings;

        public bool MarkerVisible => marker != null && marker.activeInHierarchy;
        SpatialNavigationUI navigationUI;

        void Awake()
        {
            controller = GetComponent<SpatialAIController>();
            contentRoot = new GameObject("Saved Map Content").transform;
            contentRoot.gameObject.SetActive(false);
            document = SpatialUIToolkitHost.Attach(gameObject, "PlaceUI", 150);
            if (document != null)
            {
                var root = document.rootVisualElement;
                barRoot = root.Q<VisualElement>(className: "place-wrap");
                messageLabel = root.Q<Label>("placeMessage");
                markerTag = root.Q<Label>("markerTag");
                if (messageLabel != null) messageLabel.text = message;
            }
        }

        // The current entry point resolves first; maps saved before the
        // renames still carry a legacy place name.
        SpatialPlace FindPlace(SpatialMap map)
        {
            if (map == null || map.Data == null || map.Data.places == null) return null;
            var place = map.Data.places.Find(p => p.name == EntryPointName);
            if (place != null) return place;
            foreach (var legacy in LegacyPlaceNames)
            {
                place = map.Data.places.Find(p => p.name == legacy);
                if (place != null) return place;
            }
            return null;
        }

        void LateUpdate()
        {
            UpdateBarVisibility();
            if (controller?.Map == null) { contentRoot.gameObject.SetActive(false); return; }
            var place = FindPlace(controller.Map);
            var tracking = SpatialARTracking.TryGetPoseValidity(controller.Origin?.Camera, out _);
            var valid = tracking && controller.CaptureOriginValid;
            var position = Vector3.zero; var rotation = Quaternion.identity;
            if (!valid && tracking && controller.Session != null
                && controller.Session.TryMapToWorld(Vector3.zero, out position)
                && controller.Session.TryMapToWorld(Vector3.forward, out var forward)
                && controller.Session.TryMapToWorld(Vector3.up, out var up))
            { rotation = Quaternion.LookRotation(forward-position, up-position); valid = true; }
            contentRoot.gameObject.SetActive(valid && place != null);
            if (!valid || place == null) { SpatialUIToolkitHost.Show(markerTag, false); return; }
            contentRoot.SetPositionAndRotation(position, rotation);
            if (marker == null)
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere); marker.name = place.name;
                Destroy(marker.GetComponent<SphereCollider>());
                marker.transform.SetParent(contentRoot, false); marker.transform.localScale = Vector3.one * .08f;
                markerMaterial = new Material(Shader.Find("Sprites/Default")) { color = Color.green };
                marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            }
            // Only the root applies map-to-world; children always retain map coordinates.
            marker.transform.localPosition = place.mapPosition;
            UpdateMarkerTag();
        }

        void UpdateMarkerTag()
        {
            if (markerTag == null || marker == null || controller.Origin?.Camera == null)
            { SpatialUIToolkitHost.Show(markerTag, false); return; }
            var pixel = controller.Origin.Camera.WorldToScreenPoint(marker.transform.position);
            if (pixel.z <= 0 || !MarkerVisible) { SpatialUIToolkitHost.Show(markerTag, false); return; }
            SpatialUIToolkitHost.Show(markerTag, true);
            markerTag.style.left = pixel.x - 52;
            markerTag.style.top = Screen.height - pixel.y - 44;
        }

        void UpdateBarVisibility()
        {
            if (document == null) return;
            bool show = !SpatialBrandedStartup.Visible
                && controller?.Map != null
                && !(controller.Navigation != null && controller.Navigation.HidesLegacyOverlay);
            if (show)
            {
                if (settings == null) settings = FindAnyObjectByType<SpatialSettingsUI>();
                if (settings != null && settings.IsOpen) show = false;
                // The open Telemetry log covers the bottom-center bar area:
                // yield the space rather than overlap Set Entry Point.
                if (show)
                {
                    if (navigationUI == null) navigationUI = FindAnyObjectByType<SpatialNavigationUI>();
                    if (navigationUI != null && navigationUI.LogOpen) show = false;
                }
            }
            document.rootVisualElement.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (messageLabel != null && messageLabel.text != CurrentMessage()) messageLabel.text = CurrentMessage();
        }

        string CurrentMessage()
        {
            if (controller?.Map != null
                && FindPlace(controller.Map) != null
                && !MarkerVisible)
                return EntryPointName + " hidden — valid tracking and map alignment required.";
            return message;
        }

        void OnDisable() { if (contentRoot != null) contentRoot.gameObject.SetActive(false); }
        void OnDestroy()
        {
            if (contentRoot != null) Destroy(contentRoot.gameObject);
            if (markerMaterial != null) Destroy(markerMaterial);
        }
    }
}

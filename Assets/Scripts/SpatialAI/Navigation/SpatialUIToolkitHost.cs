using UnityEngine;
using UnityEngine.UIElements;

namespace SpatialAI
{
    // Shared UI Toolkit bootstrap: loads the PanelSettings, UXML tree, and
    // USS theme from Resources/SpatialUI and hosts them on a UIDocument.
    // Every SpatialAI surface (navigation, settings, place) renders through
    // this path — no uGUI or IMGUI remains in the app UI.
    public static class SpatialUIToolkitHost
    {
        public const string ResourceRoot = "SpatialUI/";

        public static UIDocument Attach(GameObject host, string uxmlName, int sortingOrder)
        {
            var settings = Resources.Load<PanelSettings>(ResourceRoot + "SpatialPanelSettings");
            var tree = Resources.Load<VisualTreeAsset>(ResourceRoot + uxmlName);
            var sheet = Resources.Load<StyleSheet>(ResourceRoot + "SpatialUI");
            if (settings == null || tree == null || sheet == null)
            {
                Debug.LogError("[SpatialAI UI] Missing UI Toolkit resources for " + uxmlName +
                    " (settings=" + (settings != null) + " tree=" + (tree != null) +
                    " sheet=" + (sheet != null) + ").");
                return null;
            }
            // One UIDocument per surface on its own child object: several
            // SpatialAI UIs share the rig GameObject and must never share
            // (and clear) the same document.
            var documentObject = new GameObject(uxmlName + " Document");
            documentObject.transform.SetParent(host.transform, false);
            var document = documentObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = sortingOrder;
            var root = document.rootVisualElement;
            root.Clear();
            tree.CloneTree(root);
            // NOTE: document.visualTreeAsset is deliberately left unset.
            // PlayMode verification proved that assigning it makes UIDocument
            // regenerate (and empty) the manually-cloned root a few frames
            // after Attach, wiping all three surfaces mid-session. Callers
            // query the tree synchronously right after Attach, so the manual
            // clone stays the single source of content.
            root.styleSheets.Add(sheet);
            root.pickingMode = PickingMode.Ignore;
            // Mobile safe area (notches / gesture bars): the
            // SpatialAISafeArea engine insets the whole surface and
            // refreshes it as orientation and cutouts change.
            documentObject.AddComponent<SpatialAISafeArea>();
            // Responsive breakpoints: USS has no media queries, so layout
            // size drives .compact / .wide classes on the root.
            root.RegisterCallback<GeometryChangedEvent>(evt => UpdateBreakpoints(root));
            root.schedule.Execute(() => UpdateBreakpoints(root)).StartingIn(200);
            return document;
        }

        // Reload/reimport guard: long Editor sessions (domain reload,
        // asset reimport) can empty a manually-cloned root while the host
        // lives — UIDocument only regenerates roots it owns via
        // visualTreeAsset, which we deliberately leave unset. The document
        // object is named "<uxml> Document", so the tree can be restored
        // without any extra state. Called from the per-surface safe-area
        // tick; callers holding pre-wipe element references re-query.
        public static void Reattach(UIDocument document)
        {
            if (document == null) return;
            var root = document.rootVisualElement;
            if (root == null || root.childCount > 0) return;
            const string suffix = " Document";
            string name = document.gameObject.name;
            if (!name.EndsWith(suffix)) return;
            string uxml = name.Substring(0, name.Length - suffix.Length);
            var tree = Resources.Load<VisualTreeAsset>(ResourceRoot + uxml);
            var sheet = Resources.Load<StyleSheet>(ResourceRoot + "SpatialUI");
            if (tree == null || sheet == null)
            {
                Debug.LogError("[SpatialAI UI] Cannot heal emptied root for " + uxml + ".");
                return;
            }
            root.Clear();
            tree.CloneTree(root);
            root.styleSheets.Remove(sheet);
            root.styleSheets.Add(sheet);
            root.pickingMode = PickingMode.Ignore;
            UpdateBreakpoints(root);
            Debug.LogWarning("[SpatialAI UI] Healed emptied " + uxml + " root after reload/reimport.");
        }

        static void UpdateBreakpoints(VisualElement root)
        {
            float w = root.layout.width, h = root.layout.height;
            if (float.IsNaN(w) || w <= 1f || float.IsNaN(h)) return;
            bool compact = w < 950f || (w > h && h < 1350f);
            root.EnableInClassList("compact", compact);
            root.EnableInClassList("wide", !compact && w >= 1250f);
            root.EnableInClassList("portrait", h >= w);
        }

        // Show/hide helper. The hidden class carries the USS rule, but an
        // explicit inline display is set as well: PlayMode verification
        // (Settings overlay, sortingOrder 400) showed a panel whose class
        // list updated while its computed style stayed display:none, so
        // visibility must never depend on class matching alone.
        public static void Show(VisualElement element, bool show)
        {
            if (element == null) return;
            if (show)
            {
                element.RemoveFromClassList("hidden");
                element.style.display = DisplayStyle.Flex;
            }
            else
            {
                element.AddToClassList("hidden");
                element.style.display = DisplayStyle.None;
            }
            element.MarkDirtyRepaint();
        }
    }
}

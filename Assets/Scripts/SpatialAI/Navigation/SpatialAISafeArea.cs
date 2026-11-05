using UnityEngine;
using UnityEngine.UIElements;

namespace SpatialAI
{
    // Portrait-first safe-area constraint engine for every SpatialAI
    // UI Toolkit surface (navigation, settings, place). It reads
    // Screen.safeArea and applies it as padding on the UIDocument root,
    // refreshed on geometry changes and once per second so rotation,
    // punch-hole cutouts, and the Android gesture bar stay clear:
    // the header/telemetry bar floats below the notch, and bottom
    // actions (Set Entry Point, dock, telemetry chips) float above the
    // home gesture bar. Attach owns one UIDocument; the static Apply
    // keeps SpatialUIToolkitHost on the same math without duplicating it.
    [RequireComponent(typeof(UIDocument))]
    public sealed class SpatialAISafeArea : MonoBehaviour
    {
        UIDocument document;
        VisualElement root;
        IVisualElementScheduledItem refresh;
        bool hooked;
        float lastHeal = -10f;

        void OnEnable()
        {
            if (document == null) document = GetComponent<UIDocument>();
            root = document != null ? document.rootVisualElement : null;
            if (root == null) return;
            TryHeal();
            var safe = Screen.safeArea;
            Debug.Log("[SpatialAI SafeArea] screen=" + Screen.width + "x" + Screen.height
                + " dpi=" + Screen.dpi + " safeArea=" + safe
                + " layout=" + root.layout.width + "x" + root.layout.height);
            Apply(root);
            Debug.Log("[SpatialAI SafeArea] applied padding L=" + root.style.paddingLeft.value.value
                + " R=" + root.style.paddingRight.value.value
                + " T=" + root.style.paddingTop.value.value
                + " B=" + root.style.paddingBottom.value.value
                + " compact=" + root.ClassListContains("compact"));
            if (!hooked)
            {
                hooked = true;
                root.RegisterCallback<GeometryChangedEvent>(OnGeometry);
            }
            if (refresh == null || !refresh.isActive)
                refresh = root.schedule.Execute(() => { TryHeal(); Apply(root); }).Every(1000);
        }

        void OnDisable()
        {
            if (refresh != null) refresh.Pause();
            if (root != null && hooked)
            {
                root.UnregisterCallback<GeometryChangedEvent>(OnGeometry);
                hooked = false;
            }
        }

        void OnGeometry(GeometryChangedEvent evt)
        {
            if (root == null) return;
            TryHeal();
            Apply(root);
        }

        // Reload/reimport guard entry: an emptied root means the manual
        // clone was wiped while the host lives (Editor domain reload or
        // asset reimport); restore it so dialogs and buttons keep working.
        void TryHeal()
        {
            if (document == null || root == null || root.childCount > 0) return;
            if (Time.realtimeSinceStartup - lastHeal < 5f) return;
            lastHeal = Time.realtimeSinceStartup;
            SpatialUIToolkitHost.Reattach(document);
        }

        // Device truth: cutouts and system gesture bars are already
        // excluded from Screen.safeArea on Android, so pass it through
        // as root padding (converted to panel units). Breathing room
        // around bars and cards lives in SpatialUI.uss, not here.
        public static void Apply(VisualElement root)
        {
            if (root == null || Screen.width <= 0 || Screen.height <= 0) return;
            float layoutWidth = root.layout.width;
            if (float.IsNaN(layoutWidth) || layoutWidth <= 1f) return;
            float units = layoutWidth / Screen.width;
            var safe = Screen.safeArea;
            root.style.paddingLeft = safe.xMin * units;
            root.style.paddingRight = (Screen.width - safe.xMax) * units;
            root.style.paddingTop = (Screen.height - safe.yMax) * units;
            root.style.paddingBottom = safe.yMin * units;
        }
    }
}

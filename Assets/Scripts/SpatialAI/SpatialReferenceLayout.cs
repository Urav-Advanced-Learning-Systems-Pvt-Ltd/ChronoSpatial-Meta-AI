using UnityEngine;
using UnityEngine.UI;

namespace SpatialAI
{
    public sealed class SpatialReferenceLayout : MonoBehaviour
    {
        public const int StatusFontSize = 38; // 29 * 1.30, rounded.
        public const float ButtonHeight = 73; // 52 * 1.40, rounded.
        public const int ButtonFontSize = 43;
        RectTransform safeRoot;
        Rect previousSafeArea;

        public void Configure(Text diagnostics)
        {
            if (safeRoot != null || diagnostics == null) return;
            var canvas = diagnostics.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            safeRoot = new GameObject("SpatialAI Safe Area", typeof(RectTransform)).GetComponent<RectTransform>();
            safeRoot.SetParent(canvas.transform, false);
            safeRoot.anchorMin = Vector2.zero; safeRoot.anchorMax = Vector2.one; safeRoot.offsetMin = Vector2.zero; safeRoot.offsetMax = Vector2.zero;
            var panel = diagnostics.transform.parent as RectTransform;
            if (panel == null) return;
            panel.SetParent(safeRoot, false);
            var controls = GameObject.Find("Spatial AI Panel");
            if (controls != null && controls.transform.parent == canvas.transform) controls.transform.SetParent(safeRoot, false);
            // Explicit dimensions avoid cumulative scaling after reconfiguration.
            panel.anchorMin = new Vector2(.03f, .08f); panel.anchorMax = new Vector2(.562f, .70f);
            panel.offsetMin = panel.offsetMax = Vector2.zero;
            diagnostics.fontSize = StatusFontSize;
            if (controls != null)
            {
                var controlsRect = controls.GetComponent<RectTransform>();
                controlsRect.anchorMin = new Vector2(.592f, .08f); controlsRect.anchorMax = new Vector2(.97f, .70f);
                controlsRect.offsetMin = controlsRect.offsetMax = Vector2.zero;
                foreach (var button in controls.GetComponentsInChildren<Button>(true))
                {
                    var rect = button.GetComponent<RectTransform>();
                    rect.sizeDelta = new Vector2(rect.sizeDelta.x, ButtonHeight);
                    var element = button.GetComponent<LayoutElement>();
                    if (element != null) element.minHeight = element.preferredHeight = ButtonHeight;
                    var label = button.GetComponentInChildren<Text>();
                    if (label == null) continue;
                    label.fontSize = label.resizeTextMaxSize = ButtonFontSize;
                    label.resizeTextMinSize = 32;
                }
            }
            if (panel.GetComponent<RectMask2D>() == null) panel.gameObject.AddComponent<RectMask2D>();
            var scroll = panel.GetComponent<ScrollRect>();
            if (scroll == null) scroll = panel.gameObject.AddComponent<ScrollRect>();
            var content = diagnostics.rectTransform;
            content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(.5f, 1);
            content.anchoredPosition = new Vector2(0, -18); content.sizeDelta = new Vector2(-36, 0);
            diagnostics.verticalOverflow = VerticalWrapMode.Overflow;
            var fitter = diagnostics.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = diagnostics.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = panel; scroll.content = content; scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;
            ApplySafeArea();
        }
        void LateUpdate() { if (safeRoot != null && previousSafeArea != Screen.safeArea) ApplySafeArea(); }
        void ApplySafeArea()
        {
            if (Screen.width <= 0 || Screen.height <= 0) return;
            previousSafeArea = Screen.safeArea;
            safeRoot.anchorMin = new Vector2(previousSafeArea.xMin / Screen.width, previousSafeArea.yMin / Screen.height);
            safeRoot.anchorMax = new Vector2(previousSafeArea.xMax / Screen.width, previousSafeArea.yMax / Screen.height);
        }
    }
}

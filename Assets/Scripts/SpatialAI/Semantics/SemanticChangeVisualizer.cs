using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialAI
{
    // 3D Living Map visualizer for SAM 3.1 semantic entities (hackathon
    // thesis demo). Every active entity gets a 3D bounding wireframe box
    // in AR world space plus a camera-facing billboard badge:
    // green  "[SAM 3.1] <Label>" for static objects,
    // amber  "[SAM 3.1] <Label> (MOVED <distance>m)" for moved entities,
    // red    "[SAM 3.1] Obstruction" for objects on the active route.
    // Moved entities also get a glowing trail from PreviousPosition to
    // the new position. Entity positions are map-space; they are pushed
    // through the live session alignment (TryMapToWorld) when localized
    // and fall back to map==world on a fresh single session.
    public sealed class SemanticChangeVisualizer : MonoBehaviour
    {
        const int MaxVisuals = 64;
        const float RefreshInterval = 0.25f;
        const float BadgeWorldWidth = 1.0f;
        const int BadgePixelWidth = 560;
        const int BadgePixelHeight = 120;

        static readonly Color Green = new Color(0.25f, 0.9f, 0.45f, 1f);
        static readonly Color Amber = new Color(1f, 0.7f, 0.15f, 1f);
        static readonly Color Red = new Color(1f, 0.28f, 0.28f, 1f);

        struct EntityVisual
        {
            public GameObject root;
            public LineRenderer box;
            public LineRenderer trail;
            public Transform badge;
            public Text badgeText;
            public Image badgeBack;
        }

        SpatialAIController controller;
        SpatialMapReconciler reconciler;
        Material lineMaterial;
        Texture2D badgePixel;
        readonly Dictionary<string, EntityVisual> visuals = new Dictionary<string, EntityVisual>();
        float nextRefresh;

        public static SemanticChangeVisualizer GetOrCreate()
        {
            var existing = FindAnyObjectByType<SemanticChangeVisualizer>();
            if (existing != null) return existing;
            return new GameObject("Semantic Change Visualizer").AddComponent<SemanticChangeVisualizer>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<SemanticChangeVisualizer>() != null) return;
            new GameObject("Semantic Change Visualizer", typeof(SemanticChangeVisualizer));
        }

        void Awake()
        {
            lineMaterial = new Material(Shader.Find("Sprites/Default"));
            badgePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            badgePixel.SetPixel(0, 0, Color.white);
            badgePixel.Apply();
        }

        void OnEnable()
        {
            if (reconciler == null) reconciler = FindAnyObjectByType<SpatialMapReconciler>();
            if (reconciler != null) reconciler.OnPhysicalChangeDetected += OnEntityChanged;
        }

        void OnDisable()
        {
            if (reconciler != null) reconciler.OnPhysicalChangeDetected -= OnEntityChanged;
        }

        void OnDestroy()
        {
            if (lineMaterial != null) Destroy(lineMaterial);
            if (badgePixel != null) Destroy(badgePixel);
        }

        void OnEntityChanged(SemanticEntity3D entity)
        {
            // Rebuild immediately so judges see the change the moment SAM
            // reports it instead of at the next poll tick.
            nextRefresh = 0f;
        }

        void Update()
        {
            if (controller == null) controller = FindAnyObjectByType<SpatialAIController>();
            var camera = Camera.main;
            if (camera != null)
            {
                var facing = camera.transform.rotation;
                foreach (var visual in visuals.Values)
                    if (visual.badge != null) visual.badge.rotation = facing;
            }
            if (Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = Time.realtimeSinceStartup + RefreshInterval;
            Refresh();
        }

        // Confirmation, not visibility: every sighting renders immediately so
        // feedback is never invisible, but a first (possibly sloppy) mask
        // draws provisional grey. Two agreeing sightings - the reconciler's
        // running mean pushing observationCount to 2+ - flip it to its real
        // status color. Agreement still guards correctness; visibility no
        // longer waits for it.
        public static bool IsConfirmed(SemanticEntity3D entity)
        {
            return entity != null && entity.observationCount >= 2;
        }

        static readonly Color Provisional = new Color(0.62f, 0.64f, 0.70f, 1f);

        public static Color BoxColor(bool confirmed, bool obstructed, bool moved)
        {
            if (!confirmed) return Provisional;
            if (obstructed) return Red;
            if (moved) return Amber;
            return Green;
        }

        void Refresh()
        {
            var map = controller != null ? controller.Map : null;
            if (map == null) { Clear(); return; }
            var session = controller.Session;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var entity in map.SemanticEntitiesSnapshot())
            {
                if (entity == null || entity.Status == SemanticEntityStatus.Removed) continue;

                if (string.IsNullOrEmpty(entity.entityId)) continue;
                if (count >= MaxVisuals) break;
                count++;
                seen.Add(entity.entityId);
                if (!visuals.TryGetValue(entity.entityId, out var visual))
                {
                    visual = Create(entity.entityId);
                    visuals[entity.entityId] = visual;
                }
                UpdateVisual(visual, entity, session);
            }
            List<string> dead = null;
            foreach (var id in visuals.Keys)
                if (!seen.Contains(id)) (dead ??= new List<string>()).Add(id);
            if (dead == null) return;
            foreach (var id in dead)
            {
                if (visuals.TryGetValue(id, out var visual) && visual.root != null)
                    Destroy(visual.root);
                visuals.Remove(id);
            }
        }

        void Clear()
        {
            foreach (var visual in visuals.Values)
                if (visual.root != null) Destroy(visual.root);
            visuals.Clear();
        }

        static Vector3 ToWorld(Vector3 mapPosition, SpatialSDKSession session)
        {
            if (session != null && session.TryMapToWorld(mapPosition, out var world)) return world;
            return mapPosition;
        }

        EntityVisual Create(string entityId)
        {
            var root = new GameObject("Entity " + entityId);
            root.transform.SetParent(transform, false);
            var box = root.AddComponent<LineRenderer>();
            box.sharedMaterial = lineMaterial;
            box.useWorldSpace = true;
            box.positionCount = 24;
            box.widthMultiplier = 0.02f;
            box.numCapVertices = 4;
            box.numCornerVertices = 4;
            var trail = new GameObject("Trail").AddComponent<LineRenderer>();
            trail.transform.SetParent(root.transform, false);
            trail.sharedMaterial = lineMaterial;
            trail.useWorldSpace = false;
            trail.positionCount = 2;
            trail.widthMultiplier = 0.05f;
            trail.numCapVertices = 6;
            trail.numCornerVertices = 6;
            var badgeObject = new GameObject("Badge");
            badgeObject.transform.SetParent(root.transform, false);
            var canvas = badgeObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = badgeObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(BadgePixelWidth, BadgePixelHeight);
            badgeObject.transform.localScale = Vector3.one * (BadgeWorldWidth / BadgePixelWidth);
            var backObject = new GameObject("Back");
            backObject.transform.SetParent(badgeObject.transform, false);
            var backRect = backObject.AddComponent<RectTransform>();
            backRect.anchorMin = Vector2.zero;
            backRect.anchorMax = Vector2.one;
            backRect.offsetMin = Vector2.zero;
            backRect.offsetMax = Vector2.zero;
            var back = backObject.AddComponent<Image>();
            back.sprite = Sprite.Create(badgePixel, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            var textObject = new GameObject("Text");
            textObject.transform.SetParent(badgeObject.transform, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var text = textObject.AddComponent<Text>();
            // Unity 6000 removed Arial.ttf as a built-in font; asking for
            // it logs an error every time and yields no font, which is how
            // the badges silently disappeared. LegacyRuntime is the valid
            // built-in; a one-time warning covers the impossible null.
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null)
                Debug.LogWarning("[SpatialAI Visuals] built-in badge font missing; badges hidden.");
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            // Long MOVED strings must shrink into the badge instead of
            // overflowing its background on camera.
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 22;
            text.resizeTextMaxSize = 40;
            text.fontSize = 40;
            var outline = textObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(2f, -2f);
            return new EntityVisual { root = root, box = box, trail = trail, badge = badgeObject.transform, badgeText = text, badgeBack = back };
        }

        void UpdateVisual(EntityVisual visual, SemanticEntity3D entity, SpatialSDKSession session)
        {
            bool obstructed = entity.Status == SemanticEntityStatus.NewObstruction
                || (reconciler != null && reconciler.IsOnActiveRoute(entity.position));
            bool moved = !obstructed && entity.Status == SemanticEntityStatus.Moved;
            bool confirmed = IsConfirmed(entity);
            Color color = BoxColor(confirmed, obstructed, moved);
            string badge = !confirmed ? "[SAM 3.1] " + entity.label + " (confirming...)"
                : obstructed ? "[SAM 3.1] Obstruction"
                : moved ? "[SAM 3.1] " + entity.label + " (MOVED " + entity.DisplacementMeters.ToString("F1") + "m)"
                : "[SAM 3.1] " + entity.label;

            Vector3 center = ToWorld(entity.position, session);
            Vector3 half = entity.extent * 0.5f;
            half.x = Mathf.Max(0.03f, half.x);
            half.y = Mathf.Max(0.03f, half.y);
            half.z = Mathf.Max(0.03f, half.z);
            // Corners run along the capture-camera basis, not world axes:
            // a portrait-held phone no longer draws a vertical door as a
            // horizontal box. Each corner maps through the live alignment
            // on its own so localized maps stay exact. Legacy entities
            // carry identity rotation and render exactly as before.
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                Vector3 offset = entity.rotation * new Vector3(
                    (i & 1) == 0 ? -half.x : half.x,
                    (i & 2) == 0 ? -half.y : half.y,
                    (i & 4) == 0 ? -half.z : half.z);
                corners[i] = ToWorld(entity.position + offset, session);
            }
            int[] edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            var line = new Vector3[24];
            for (int i = 0; i < 24; i++) line[i] = corners[edges[i]];
            visual.box.SetPositions(line);
            visual.box.startColor = color;
            visual.box.endColor = color;
            visual.box.enabled = true;

            if (moved)
            {
                visual.trail.transform.SetPositionAndRotation(center, Quaternion.identity);
                visual.trail.SetPosition(0, ToWorld(entity.PreviousPosition, session) - center);
                visual.trail.SetPosition(1, Vector3.zero);
                visual.trail.startColor = Amber;
                visual.trail.endColor = new Color(Amber.r, Amber.g, Amber.b, 0.25f);
                visual.trail.enabled = true;
            }
            else visual.trail.enabled = false;

            visual.badge.SetPositionAndRotation(center + Vector3.up * (half.y + 0.28f), visual.badge.rotation);
            visual.badgeText.text = badge;
            visual.badgeBack.color = new Color(color.r * 0.28f, color.g * 0.28f, color.b * 0.28f, 0.92f);
        }
    }
}

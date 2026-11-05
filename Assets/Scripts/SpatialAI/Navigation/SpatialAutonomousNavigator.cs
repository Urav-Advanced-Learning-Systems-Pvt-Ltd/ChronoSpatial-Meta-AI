using System;
using UnityEngine;

namespace SpatialAI
{
    // Autonomous guidance without a walked route. Synthesizes waypoints from
    // the keyframe graph to a semantic entity, renders them through the
    // existing SpatialRouteArrows, and reuses SpatialRouteFollower arrival:
    // within 0.75 m of the destination the arrows dismiss and SpatialSpeech
    // announces the arrival. Runs only while no recorded route is active.
    public sealed class SpatialAutonomousNavigator : MonoBehaviour
    {
        public event Action<string> ArrivedAt;
        public bool Active { get; private set; }
        public string TargetLabel { get; private set; }
        SpatialAIController controller;
        SpatialRouteArrows arrows;
        SpatialSpeech speech;
        SpatialRouteFollower follower;

        public static SpatialAutonomousNavigator GetOrCreate(SpatialAIController controller)
        {
            var nav = controller.GetComponent<SpatialAutonomousNavigator>();
            if (nav == null) nav = controller.gameObject.AddComponent<SpatialAutonomousNavigator>();
            return nav;
        }

        void Awake()
        {
            controller = GetComponent<SpatialAIController>();
            var arrowsObject = new GameObject("Autonomous arrows");
            arrowsObject.transform.SetParent(transform, false);
            arrows = arrowsObject.AddComponent<SpatialRouteArrows>();
            speech = GetComponent<SpatialSpeech>() ?? FindAnyObjectByType<SpatialSpeech>();
        }

        // Starts guidance to the named entity. Returns a user-facing failure, or null.
        public string NavigateToLabel(string label)
        {
            if (controller == null) return "Navigation is unavailable.";
            if (string.IsNullOrWhiteSpace(label)) return "Pick a destination landmark.";
            var nav = controller.Navigation;
            if (nav != null && nav.Mode != SpatialNavigationController.Flow.Idle)
                return "Finish the current route first.";
            if (controller.Map == null) return "Scan or load a map first.";
            if (!controller.Session.TryGetMapPose(out var pose)) return "Localize the map before navigating.";
            SemanticEntity3D nearest = null;
            float nearestSqr = float.PositiveInfinity;
            foreach (var entity in controller.Map.SemanticEntitiesSnapshot())
            {
                if (entity == null || entity.Status == SemanticEntityStatus.Removed) continue;
                if (!string.Equals(entity.label, label, StringComparison.OrdinalIgnoreCase)) continue;
                float sqr = (entity.position - pose.position).sqrMagnitude;
                if (sqr < nearestSqr) { nearestSqr = sqr; nearest = entity; }
            }
            if (nearest == null) return "No '" + label + "' found in this map.";
            return NavigateToEntity(nearest);
        }

        public string NavigateToEntity(SemanticEntity3D entity)
        {
            if (controller == null) return "Navigation is unavailable.";
            var nav = controller.Navigation;
            if (nav != null && nav.Mode != SpatialNavigationController.Flow.Idle)
                return "Finish the current route first.";
            if (entity == null || string.IsNullOrWhiteSpace(entity.label)) return "Unknown destination.";
            if (controller.Map == null) return "Scan or load a map first.";
            if (!controller.Session.TryGetMapPose(out var pose)) return "Localize the map before navigating.";
            Vector3 start = SpatialNavigationRoute.Ground(pose.position, pose.position.y);
            Vector3 target = SpatialNavigationRoute.Ground(entity.position, pose.position.y);
            var points = SpatialWaypointSynthesizer.Synthesize(controller.Map.Data, start, target, out var failure);
            if (points == null) return failure;
            var route = new SpatialNavigationRoute
            {
                destination = entity.label,
                floorY = pose.position.y,
                points = new System.Collections.Generic.List<Vector3>(points)
            };
            follower = new SpatialRouteFollower(route);
            if (arrows == null)
            {
                var arrowsObject = new GameObject("Autonomous arrows");
                arrowsObject.transform.SetParent(transform, false);
                arrows = arrowsObject.AddComponent<SpatialRouteArrows>();
            }
            if (speech == null) speech = GetComponent<SpatialSpeech>() ?? FindAnyObjectByType<SpatialSpeech>();
            Active = true;
            TargetLabel = entity.label;
            Debug.Log("[SpatialAI Autonomy] navigating to " + entity.label + " waypoints=" + points.Count);
            return null;
        }

        public void Stop()
        {
            Active = false;
            follower = null;
            TargetLabel = null;
            if (arrows != null) arrows.Hide();
        }

        void LateUpdate()
        {
            if (!Active || follower == null || controller == null || controller.Map == null) return;
            if (!controller.Session.TryGetMapPose(out var pose)) { arrows.Hide(); return; }
            if (!follower.Update(pose.position, true)) { arrows.Hide(); return; }
            if (follower.Arrived)
            {
                arrows.Hide();
                Active = false;
                var label = TargetLabel;
                follower = null;
                TargetLabel = null;
                if (speech != null) speech.Speak("You have reached " + label + ".");
                ArrivedAt?.Invoke(label);
                return;
            }
            arrows.Render(follower, controller.Session);
        }

        void OnDisable() { Stop(); }
    }
}

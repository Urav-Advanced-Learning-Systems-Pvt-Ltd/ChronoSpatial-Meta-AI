#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialAI {
    /// <summary>Provides simple keyboard locomotion alongside XR Simulation's mouse look.</summary>
    public sealed class SpatialSimulationCameraController : MonoBehaviour {
        const float MoveSpeed = 1.5f;

        void LateUpdate() {
            var keyboard = Keyboard.current;
            if (keyboard == null || !Application.isPlaying) return;

            var direction = Vector3.zero;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) direction += Vector3.forward;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) direction += Vector3.back;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) direction += Vector3.left;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) direction += Vector3.right;
            if (keyboard.eKey.isPressed) direction += Vector3.up;
            if (keyboard.qKey.isPressed) direction += Vector3.down;
            if (direction.sqrMagnitude == 0f) return;

            var horizontal = transform.rotation * new Vector3(direction.x, 0f, direction.z);
            var origin = GetComponentInParent<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null) origin.transform.position += (horizontal + Vector3.up * direction.y) * MoveSpeed * Time.deltaTime;
        }
    }
}
#endif

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

namespace SpatialAI
{
    public static class ARCoreImagePose
    {
        public const string Source = "ARCore-physical-image-pose-v3";
        public const string LegacySource = "ARFoundation-native-row-order-v2";
        public const string IncompatibleMessage = "Map uses unreconciled display-camera/image axes; preserve it and make a fresh scan with physical image poses.";
        [StructLayout(LayoutKind.Sequential)] struct NativeFrame { public int version; public IntPtr frame; }

        // ArCamera_getPose is the physical, image-readout pose at exposure. In contrast,
        // ArCamera_getDisplayOrientedPose is for rendering and differs by a local Z rotation.
        // Use the existing ARCore library/session, never create or update another session.
        public static bool TryAcquire(ARCameraManager manager, Camera camera, double imageSeconds, float receiptSeconds,
            out SpatialPose opticalWorld, out SpatialPose displayWorld, out string evidence)
        {
            opticalWorld = displayWorld = default;
            evidence = "Physical image pose is unavailable on this platform";
#if UNITY_ANDROID
            if (Application.platform != RuntimePlatform.Android) return false;
            var loader = XRGeneralSettings.Instance == null ? null : XRGeneralSettings.Instance.Manager?.activeLoader;
            var subsystem = loader?.GetLoadedSubsystem<XRSessionSubsystem>() as UnityEngine.XR.ARCore.ARCoreSessionSubsystem;
            if (subsystem == null || !subsystem.running || manager?.subsystem == null || camera == null) return false;
            var session = subsystem.session.AsIntPtr();
            if (session == IntPtr.Zero) return false;
            var parameters = new XRCameraParams { zNear = camera.nearClipPlane, zFar = camera.farClipPlane,
                screenWidth = Screen.width, screenHeight = Screen.height, screenOrientation = Screen.orientation };
            if (!manager.subsystem.TryGetLatestFrame(parameters, out var frame) || frame.nativePtr == IntPtr.Zero)
            { evidence = "ARCore native camera frame unavailable"; return false; }
            // Layout/version documented in ARCore 6.4.3 Includes~/UnityXRNativePtrs.h.
            if (Marshal.ReadInt32(frame.nativePtr) != 1) { evidence = "Unsupported ARCore native frame layout"; return false; }
            var native = Marshal.PtrToStructure<NativeFrame>(frame.nativePtr);
            if (native.frame == IntPtr.Zero) return false;
            IntPtr nativeCamera = IntPtr.Zero, pose = IntPtr.Zero;
            try
            {
                ArFrame_getTimestamp(session, native.frame, out var timestampNs);
                if (!SameImage(timestampNs, imageSeconds))
                { evidence = $"Image/pose timestamp mismatch: frameNs={timestampNs} cpuSeconds={imageSeconds:R}"; return false; }
                ArFrame_acquireCamera(session, native.frame, out nativeCamera);
                if (nativeCamera == IntPtr.Zero) return false;
                ArCamera_getTrackingState(session, nativeCamera, out var tracking);
                if (tracking != 0) { evidence = "Physical camera is not tracking: " + tracking; return false; }
                ArPose_create(session, IntPtr.Zero, out pose);
                if (pose == IntPtr.Zero) return false;
                var raw = new float[7];
                ArCamera_getPose(session, nativeCamera, pose); ArPose_getPoseRaw(session, pose, raw);
                if (!TryToWorld(raw, camera.transform.parent, receiptSeconds, out opticalWorld))
                { evidence = "Invalid physical image pose or non-unit camera parent scale"; return false; }
                ArCamera_getDisplayOrientedPose(session, nativeCamera, pose); ArPose_getPoseRaw(session, pose, raw);
                if (!TryToWorld(raw, camera.transform.parent, receiptSeconds, out displayWorld))
                { evidence = "Invalid display pose"; return false; }
                var relative = Quaternion.Inverse(displayWorld.rotation) * opticalWorld.rotation;
                evidence = $"physicalPose=ArCamera_getPose frameNs={timestampNs} cpuSeconds={imageSeconds:R} displayFromOpticalRotation={relative:F5} screen={Screen.orientation} displayTransformDeltaM={Vector3.Distance(displayWorld.position,camera.transform.position):F5} displayTransformDeltaDeg={Quaternion.Angle(displayWorld.rotation,camera.transform.rotation):F4}";
                return true;
            }
            catch (DllNotFoundException exception) { evidence = exception.GetType().Name + ": ARCore physical pose API unavailable"; return false; }
            catch (EntryPointNotFoundException exception) { evidence = exception.GetType().Name + ": ARCore physical pose API unavailable"; return false; }
            finally
            {
                if (pose != IntPtr.Zero) ArPose_destroy(pose);
                if (nativeCamera != IntPtr.Zero) ArCamera_release(nativeCamera);
            }
#else
            return false;
#endif
        }

        public static bool SameImage(long frameNanoseconds, double imageSeconds) => frameNanoseconds > 0
            && SpatialFrameGeometry.Finite(imageSeconds) && Math.Abs(frameNanoseconds * 1e-9 - imageSeconds) <= 1e-6;

        public static bool TryToWorld(float[] raw, Transform parent, float receiptSeconds, out SpatialPose result)
        {
            result = default;
            if (raw == null || raw.Length != 7) return false;
            // Reflect OpenGL world and camera Z axes to Unity: S R S, S t.
            var position = new Vector3(raw[4], raw[5], -raw[6]);
            var rotation = new Quaternion(-raw[0], -raw[1], raw[2], raw[3]);
            if (parent != null)
            {
                if ((parent.lossyScale - Vector3.one).sqrMagnitude > 1e-8f) return false;
                position = parent.TransformPoint(position); rotation = parent.rotation * rotation;
            }
            result = new SpatialPose(position, rotation, receiptSeconds);
            return SpatialFrameGeometry.ValidPose(result);
        }

#if UNITY_ANDROID
        const string Library = "arcore_sdk_c";
        [DllImport(Library)] static extern void ArFrame_getTimestamp(IntPtr session, IntPtr frame, out long timestampNs);
        [DllImport(Library)] static extern void ArFrame_acquireCamera(IntPtr session, IntPtr frame, out IntPtr camera);
        [DllImport(Library)] static extern void ArCamera_getTrackingState(IntPtr session, IntPtr camera, out int state);
        [DllImport(Library)] static extern void ArPose_create(IntPtr session, IntPtr raw, out IntPtr pose);
        [DllImport(Library)] static extern void ArPose_destroy(IntPtr pose);
        [DllImport(Library)] static extern void ArPose_getPoseRaw(IntPtr session, IntPtr pose, [Out] float[] raw);
        [DllImport(Library)] static extern void ArCamera_getPose(IntPtr session, IntPtr camera, IntPtr pose);
        [DllImport(Library)] static extern void ArCamera_getDisplayOrientedPose(IntPtr session, IntPtr camera, IntPtr pose);
        [DllImport(Library)] static extern void ArCamera_release(IntPtr camera);
#endif
    }
}

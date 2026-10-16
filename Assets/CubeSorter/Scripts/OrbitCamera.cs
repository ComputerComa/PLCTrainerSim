using System.Collections.Generic;
using UnityEngine;

namespace CubeSorter
{
    /// <summary>Orbit camera using IMGUI events (works with either input backend). Drag = orbit, Shift/middle-drag = pan, wheel = zoom.
    /// MachineView registers UI rectangles in Blockers so dragging over a panel does not move the camera, and calls GoTo() for presets.</summary>
    public class OrbitCamera : MonoBehaviour
    {
        public Vector3 focus = new Vector3(0f, 0.2f, 0.25f);
        public float distance = 1.6f, yaw = 0f, pitch = 25f;
        public static readonly List<Rect> Blockers = new List<Rect>();
        Vector3 tFocus; float tDist, tYaw, tPitch;

        void OnEnable() { tFocus = focus; tDist = distance; tYaw = yaw; tPitch = pitch; var c = GetComponent<Camera>(); if (c) { c.nearClipPlane = 0.02f; c.farClipPlane = 50f; } Apply(); }

        public void GoTo(Vector3 f, float d, float y, float p) { tFocus = f; tDist = d; tYaw = y; tPitch = p; }

        public void Snap() { focus = tFocus; distance = tDist; yaw = tYaw; pitch = tPitch; Apply(); }

        void LateUpdate()
        {
            float k = 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
            focus = Vector3.Lerp(focus, tFocus, k); distance = Mathf.Lerp(distance, tDist, k);
            yaw = Mathf.LerpAngle(yaw, tYaw, k); pitch = Mathf.Lerp(pitch, tPitch, k);
            Apply();
        }

        void Apply()
        {
            var rot = Quaternion.Euler(pitch, 180f + yaw, 0f); // yaw 0 = looking at the machine from the visitor side (-Y in Blender)
            transform.rotation = rot;
            transform.position = focus - rot * Vector3.forward * distance;
        }

        void OnGUI()
        {
            var e = Event.current;
            for (int i = 0; i < Blockers.Count; i++) if (Blockers[i].Contains(e.mousePosition)) return;
            if (e.type == EventType.ScrollWheel) tDist = Mathf.Clamp(tDist * (1f + e.delta.y * 0.06f), 0.12f, 6f);
            if (e.type == EventType.MouseDrag)
            {
                if (e.button == 2 || (e.button == 0 && e.shift))
                {
                    float s = tDist * 0.0012f;
                    tFocus += (-transform.right * e.delta.x + transform.up * e.delta.y) * s;
                }
                else { tYaw += e.delta.x * 0.3f; tPitch = Mathf.Clamp(tPitch + e.delta.y * 0.3f, -5f, 89f); }
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Apocaraider
{
    // Sandstorms ([Senses] StormSight / StormHearing / StormRadius, hidden). A storm is the moving "SandStorm" object the game keeps in
    // NewGO_ArrayList's ArrayList_Sandstorms (spawned by Sandstorm_N POIs, restored with saves, removed by the game's own "disable
    // sandstorm" option). The game decides "the player is in the storm" by distance to that object: its SandPlayer FSM switches the sand
    // around the player on within 2500 m (asset read 2026-10-02) - the same test here, flat distance, against the list (0-2 storms).
    // Refresh() runs once a second from Senses' maintenance; the list object is looked up once per scene.
    internal static class Storm
    {
        private static readonly List<Transform> _storms = new List<Transform>();
        private static PlayMakerArrayListProxy _list;
        private static float _nextFind;

        internal static bool Any { get { return _storms.Count > 0; } }
        internal static float SightFactor { get { return Mathf.Clamp01(Plugin.StormSight.Value / 100f); } }
        internal static float HearingFactor { get { return Mathf.Clamp01(Plugin.StormHearing.Value / 100f); } }

        internal static void Reset() { _storms.Clear(); _list = null; _nextFind = 0f; }

        internal static void Refresh()
        {
            _storms.Clear();
            if (_list == null)
            {
                if (Time.unscaledTime < _nextFind) return;
                _nextFind = Time.unscaledTime + 10f;
                var reg = GameObject.Find("NewGO_ArrayList");
                if (reg == null) return;
                foreach (var p in reg.GetComponents<PlayMakerArrayListProxy>())
                    if (p != null && p.referenceName == "ArrayList_Sandstorms") { _list = p; break; }
                if (_list == null) return;
            }
            var al = _list.arrayList;
            if (al == null) return;
            for (int i = 0; i < al.Count; i++)
            {
                var go = al[i] as GameObject;
                if (go != null && go.activeInHierarchy) _storms.Add(go.transform);
            }
        }

        internal static bool In(Vector3 p)
        {
            if (_storms.Count == 0) return false;
            float r = Plugin.StormRadius.Value, r2 = r * r;
            for (int i = 0; i < _storms.Count; i++)
            {
                var t = _storms[i];
                if (t == null) continue;
                Vector3 d = t.position - p; d.y = 0f;
                if (d.sqrMagnitude <= r2) return true;
            }
            return false;
        }
    }
}

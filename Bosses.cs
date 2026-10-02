using System;
using System.Collections.Generic;
using UnityEngine;

namespace Apocaraiders
{
    // [Gunplay] AdjustHumanBossHP: the human bosses' health in % of the game's own (Duke Ironjaw 1500, Buzzgut 600; asset read 2026-10-02).
    // Bosses carry the FSM "BossUI" (it shows the Health number). Every 3 s the live bosses are found and their Health capped at base x % -
    // a cap, not a multiplication, so it is safe with the game saving and reloading their Health (saveItemVar) and with any number of checks.
    // Above 100 % an unhurt boss (Health = base) is raised once per instance.
    internal static class Bosses
    {
        private static readonly Dictionary<string, float> Base = new Dictionary<string, float> { { "Duke_Ironjaw", 1500f }, { "Buzzgut", 600f } };
        private static readonly HashSet<int> _raised = new HashSet<int>();
        private static float _next;

        public static void OnSceneLoaded() { _raised.Clear(); _next = 0f; }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 3f;
            float pct = Mathf.Max(1f, Plugin.BossHpPercent.Value) / 100f;
            foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (f == null || f.FsmName != "BossUI") continue;
                var go = f.gameObject;
                string name = go.name; int cut = name.IndexOf('(');
                if (cut > 0) name = name.Substring(0, cut);
                float b;
                if (!Base.TryGetValue(name, out b)) continue;
                PlayMakerFSM health = null;
                foreach (var h in go.GetComponents<PlayMakerFSM>()) if (h != null && h.FsmName == "Health") { health = h; break; }
                if (health == null || health.Fsm == null || !health.Fsm.Initialized) continue;
                var v = health.FsmVariables.FindFsmFloat("Health");
                if (v == null || v.Value < 1f) continue;
                float target = b * pct;
                int id = go.GetInstanceID();
                if (v.Value > target + 0.5f)
                {
                    Plugin.Verbose("Bosses: " + go.name + " health " + v.Value.ToString("0") + " -> " + target.ToString("0") + " (" + (pct * 100f).ToString("0") + " % of " + b.ToString("0") + ")");
                    v.Value = target;
                }
                else if (pct > 1f && !_raised.Contains(id) && v.Value >= b - 0.5f)
                {
                    v.Value = target;
                    Plugin.Verbose("Bosses: " + go.name + " health raised to " + target.ToString("0"));
                }
                _raised.Add(id);
            }
        }
    }
}

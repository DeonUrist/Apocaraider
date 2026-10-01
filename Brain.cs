using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Apocaraiders
{
    // How ground NPCs move while they have a target ("the brain").
    //
    // Vanilla (asset dump 2026-10-01, every human and ground animal): while the Detection FSM has a detectedObj the Attack FSM sits in its
    // "trigger" state and the Movement FSM runs the NPC forward at 5-6 m/s (SetVelocity local z, every frame). Nothing turns the body toward
    // the target: the two LookAt actions in trigger aim the AttackRaycast children (the melee / burst sensors), the body's yaw comes only from
    // the "Rotate" FSM (random +-180 deg/s for 1-3 s every 1-3 s), the Attack FSM's run/rotate states (random again) and two 1.2 m bumper
    // rays that spin it +-120 deg/s. The "Unstuck" FSM (position check every 0.3 s) answers a blocked NPC with a random spin and a 40 N hop.
    // The NPC faces the target only during the melee swing (SmoothLookAt), the burst (an instant LookAt) and the run to a Hide point. So a
    // vanilla chase is a random walk that happens to reach you; the "headless chicken".
    //
    // Here, per NPC (humans and ground animals: a root object with the Movement/Attack/Detection/Unstuck FSMs and a Rigidbody with gravity;
    // flyers have gravity off and are left alone, as is anything parented under something else, which is how Apocapatrol seats a crew in a
    // car - a bailed-out crew is a fresh root object and gets a brain at once):
    // - the body turns at [Brain] TurnRate deg/s (never snaps), toward the steered heading while moving, toward the target while holding;
    // - feelers: FeelerCount rays over +-FeelerAngle around the direction to the target, FeelerLength m, on the layers the game's bumper rays
    //   use; the clearest direction nearest the target wins (a blocked heading from a recent stuck is avoided for StuckMemorySeconds);
    // - a gunman (Tracers knows the gun) stops where it can shoot - line of sight and within [NpcAim] EngagePercent of the gun's reach - and
    //   holds there with the gun up (AimPose: the shooting animation frozen on its first frame), facing the target; the game's own burst logic
    //   and the Aim pacing do the shooting; every HoldRecheck it
    //   rolls AdvanceChance % to run at the target for AdvanceSeconds; it moves again when the line of sight is lost or the target walks out
    //   of the engage distance. Melee NPCs run at the target with the feelers.
    // - stuck (the Unstuck FSM's detector is kept, its spin + hop are not): a gunman with a line of sight and the target in reach holds and
    //   shoots from there; otherwise it backs up for StuckBackupSeconds, remembers the heading as blocked and leaves on the clearer side;
    //   StuckGiveUpCount stucks within 20 s -> it stands for 3 s (facing the target), then tries again.
    // - the random turns and bumper rays are switched off while a brain is in charge; Enabled = false gives the vanilla behaviour back.
    // Cost: one Think per NPC every 0.1 s (<= 5 brains) .. 0.5 s (> 20), staggered; a moving NPC casts FeelerCount (+1 drop check) rays
    // per Think, a holding gunman one line-of-sight ray; the per-frame work is one RotateTowards per NPC. NPCs beyond MaxDistance run vanilla.
    //
    // Hooks (Harmony prefixes; every one returns at once unless the FSM belongs to an NPC with an active brain):
    //   SetVelocity.DoSetVelocity (Movement FSM) - the pedal: forward, zero or backing;      Rotate.DoRotate (Rotate + Attack FSMs) - no random yaw;
    //   Raycast.DoRaycast (Attack, the 1.2 m bumper rays) - report clear;                 LookAt.DoLookAt (Attack, owner) - our turn rate instead;
    //   SendEvent.OnEnter (Attack "Animal_Run" while holding -> "Animal_Idle"; Unstuck "Animal_rotateRandom" -> our stuck handling);
    //   AddForce.DoAddForce (Unstuck) - no hop.
    internal static class Brain
    {
        internal enum Mode { Off, Chase, Hold, Advance, BackUp, Rest }

        private sealed class Npc
        {
            public GameObject Owner; public Transform T; public Rigidbody Rb; public Collider Col;
            public PlayMakerFSM Attack, Movement;
            public FsmGameObject Target;              // Detection.detectedObj
            public bool Ranged; public Tracers.Kind Kind;
            public Animator Anim; public string AimState; public bool Frozen;   // the shooting animation held on its first frame = aiming
            public Mode Mode; public float ModeUntil;
            public float NextTick, NextRecheck, Stagger;
            public float Heading; public bool HasHeading;   // steered world yaw, degrees
            public float BlockedYaw, BlockedUntil;
            public int Stucks; public float FirstStuck;
            public float LosLostAt = -1f; public bool Los; public float Dist;
            public float LastLog;
        }

        private static readonly Dictionary<int, Npc> _npcs = new Dictionary<int, Npc>();
        private static readonly HashSet<int> _ignored = new HashSet<int>();
        private static readonly List<int> _dead = new List<int>();
        private static float _nextCount, _interval = 0.1f;
        private static int _active, _created;
        private static readonly int Mask = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 16);   // the game's bumper-ray layers (terrain, buildings, vehicle parts)
        private static float[] _angles = new float[0];
        private static readonly float[] _scores = new float[32];
        private static readonly float[] _blocks = new float[32];

        public static void OnSceneLoaded() { _npcs.Clear(); _ignored.Clear(); _active = 0; }

        internal static bool On { get { return Plugin.BrainEnabled != null && Plugin.BrainEnabled.Value; } }

        // ---------- per frame ----------
        public static void Tick()
        {
            if (_npcs.Count == 0) return;
            float now = Time.time, dt = Time.deltaTime;
            if (now >= _nextCount)
            {
                _nextCount = now + 1f;
                _dead.Clear();
                int n = 0;
                foreach (var kv in _npcs)
                {
                    if (kv.Value.Owner == null) _dead.Add(kv.Key);
                    else if (kv.Value.Mode != Mode.Off) n++;
                }
                foreach (var k in _dead) _npcs.Remove(k);
                _active = n;
                _interval = n <= 5 ? 0.1f : n <= 10 ? 0.2f : n <= 20 ? 0.3f : 0.5f;
            }
            bool on = On;
            float turn = Mathf.Max(10f, Plugin.TurnRate.Value) * dt;
            foreach (var kv in _npcs)
            {
                var n = kv.Value;
                if (n.Owner == null) continue;
                if (!on)
                {
                    if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "brain off");
                    continue;
                }
                string state;
                if (!Engaged(n, out state))
                {
                    if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "target lost");
                    continue;
                }
                if (now >= n.NextTick)
                {
                    n.NextTick = now + _interval + n.Stagger;
                    try { Think(n, now); }
                    catch (Exception e) { Plugin.Log.LogError("Brain: " + e); n.Mode = Mode.Off; continue; }
                }
                if (n.Mode == Mode.Off || n.Mode == Mode.BackUp) continue;
                if (n.Frozen && state != "trigger" && state != "run") Unfreeze(n);      // the burst (or a melee swing, a hide run): let the animation play
                if (state != "trigger" && state != "run" && state != "attack_ranged") continue;   // melee swing, hide run ...: the game's own facing
                var target = n.Target.Value;
                if (target == null) continue;
                Vector3 to;
                if (n.Mode == Mode.Hold || n.Mode == Mode.Rest || state == "attack_ranged")
                    to = target.transform.position - n.T.position;
                else if (n.HasHeading) to = Quaternion.Euler(0f, n.Heading, 0f) * Vector3.forward;
                else to = target.transform.position - n.T.position;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f) continue;
                n.T.rotation = Quaternion.RotateTowards(n.T.rotation, Quaternion.LookRotation(to, Vector3.up), turn);
            }
        }

        // the brain is in charge only while the Attack FSM runs with a target, the NPC stands on its own (not seated in a car) and is near
        private static bool Engaged(Npc n, out string state)
        {
            state = null;
            if (n.T.parent != null || n.Attack == null || !n.Attack.enabled || n.Target == null) return false;
            var fsm = n.Attack.Fsm;
            if (fsm == null || !fsm.Initialized || fsm.Finished) return false;
            if (n.Target.Value == null) return false;
            state = fsm.ActiveStateName;
            return true;
        }

        private static void Think(Npc n, float now)
        {
            var target = n.Target.Value;
            if (target == null) { if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "no target"); return; }
            Vector3 tp = target.transform.position;
            Vector3 d3 = tp - n.T.position; d3.y = 0f;
            float d = d3.magnitude;
            n.Dist = d;
            if (d > Plugin.MaxDistance.Value) { if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "far"); return; }
            if (n.Mode == Mode.Off) SetMode(n, Mode.Chase, "target at " + d.ToString("0") + " m");

            if (n.Mode == Mode.BackUp) { if (now < n.ModeUntil) return; SetMode(n, Mode.Chase, "backed up"); }
            if (n.Mode == Mode.Rest) { if (now < n.ModeUntil) return; SetMode(n, Mode.Chase, "rested"); }

            if (n.Ranged)
            {
                bool los = LineOfSight(n, target, tp);
                if (los) { n.Los = true; n.LosLostAt = -1f; }
                else { if (n.Los) { n.Los = false; n.LosLostAt = now; } }
                float reach = Tracers.RangeOf(n.Kind);
                float engage = reach * Mathf.Clamp(Plugin.EngagePercent.Value, 1f, 100f) / 100f;
                bool good = los && d <= engage;
                if (n.Mode == Mode.Hold)
                {
                    bool lost = (!los && now - n.LosLostAt > 0.5f) || d > engage * 1.1f;
                    if (lost) { SetMode(n, Mode.Chase, (los ? "target at " + d.ToString("0") + " m" : "no line of sight")); }
                    else if (now >= n.NextRecheck)
                    {
                        n.NextRecheck = now + UnityEngine.Random.Range(Mathf.Max(0.1f, Plugin.HoldRecheckMin.Value), Mathf.Max(0.1f, Plugin.HoldRecheckMax.Value));
                        if (UnityEngine.Random.Range(0f, 100f) < Plugin.AdvanceChance.Value && d > 3f)
                        {
                            n.ModeUntil = now + UnityEngine.Random.Range(Plugin.AdvanceMin.Value, Mathf.Max(Plugin.AdvanceMin.Value, Plugin.AdvanceMax.Value));
                            SetMode(n, Mode.Advance, "feels like it");
                        }
                        else return;
                    }
                    else return;
                }
                if (n.Mode == Mode.Advance)
                {
                    if (now >= n.ModeUntil || !los || d < 3f)
                    {
                        if (good) { SetMode(n, Mode.Hold, "at " + d.ToString("0") + " m" + (now >= n.ModeUntil ? "" : ", enough")); return; }
                        SetMode(n, Mode.Chase, los ? "target at " + d.ToString("0") + " m" : "no line of sight");
                    }
                }
                else if (n.Mode == Mode.Chase && good)
                {
                    n.NextRecheck = now + UnityEngine.Random.Range(Mathf.Max(0.1f, Plugin.HoldRecheckMin.Value), Mathf.Max(0.1f, Plugin.HoldRecheckMax.Value));
                    SetMode(n, Mode.Hold, "line of sight at " + d.ToString("0") + " m (engages within " + engage.ToString("0") + ")");
                    return;
                }
            }
            // moving: Chase or Advance
            Steer(n, d3, d, now);
        }

        // ---------- feelers ----------
        private static void Steer(Npc n, Vector3 toTarget, float dist, float now)
        {
            int count = Mathf.Clamp(Plugin.FeelerCount.Value, 3, 31);
            if (count % 2 == 0) count++;
            float half = Mathf.Clamp(Plugin.FeelerAngle.Value, 10f, 170f);
            if (_angles.Length != count)
            {
                _angles = new float[count];
                for (int i = 0; i < count; i++) _angles[i] = -half + half * 2f * i / (count - 1);   // symmetric, 0 in the middle
            }
            float len = Mathf.Max(0.5f, Plugin.FeelerLength.Value);
            if (dist < len) len = Mathf.Max(0.5f, dist);     // close to the target: don't "see" it as a wall
            Vector3 origin = n.Col != null ? n.Col.bounds.center : n.T.position;
            float targetYaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            Transform troot = n.Target.Value != null ? n.Target.Value.transform.root : null;
            bool blockedMem = now < n.BlockedUntil;
            RaycastHit hit;
            int best = -1; float bestScore = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float yaw = targetYaw + _angles[i];
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                float block = 0f;
                if (Physics.Raycast(origin, dir, out hit, len, Mask, QueryTriggerInteraction.Ignore))
                {
                    if (troot == null || hit.collider.transform.root != troot) block = 1f - hit.distance / len;
                }
                _blocks[i] = block;
                float score = Mathf.Cos(_angles[i] * Mathf.Deg2Rad) - block * 2.5f;
                if (blockedMem && Mathf.Abs(Mathf.DeltaAngle(yaw, n.BlockedYaw)) < 35f) score -= 1.5f;
                if (n.HasHeading && Mathf.Abs(Mathf.DeltaAngle(yaw, n.Heading)) < 12f) score += 0.15f;   // no flicker between near-equal rays
                _scores[i] = score;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (Plugin.DropCheck.Value && best >= 0)
            {
                // a short look for ground 1.5 m along the winner; a cliff or roof edge there -> the next best direction
                for (int tries = 0; tries < 3 && best >= 0; tries++)
                {
                    Vector3 dir = Quaternion.Euler(0f, targetYaw + _angles[best], 0f) * Vector3.forward;
                    Vector3 ahead = origin + dir * Mathf.Min(1.5f, len);
                    if (Physics.Raycast(ahead, Vector3.down, 4f, Mask, QueryTriggerInteraction.Ignore)) break;
                    _scores[best] = float.MinValue;
                    int nb = -1; float nbs = float.MinValue;
                    for (int i = 0; i < count; i++) if (_scores[i] > nbs) { nbs = _scores[i]; nb = i; }
                    best = nb;
                }
            }
            if (best < 0) best = count / 2;
            n.Heading = targetYaw + _angles[best];
            n.HasHeading = true;
        }

        private static bool LineOfSight(Npc n, GameObject target, Vector3 tp)
        {
            Vector3 eye = n.Col != null ? n.Col.bounds.center + Vector3.up * n.Col.bounds.extents.y * 0.6f : n.T.position + Vector3.up * 1.5f;
            Vector3 chest = tp + Vector3.up * 1f;
            RaycastHit hit;
            if (!Physics.Linecast(eye, chest, out hit, Mask, QueryTriggerInteraction.Ignore)) return true;
            return hit.collider.transform.root == target.transform.root;
        }

        // ---------- modes ----------
        private static void SetMode(Npc n, Mode m, string why)
        {
            Mode was = n.Mode;
            n.Mode = m;
            if (m == Mode.Chase || m == Mode.Advance) { n.HasHeading = false; }
            bool wasStill = was == Mode.Hold || was == Mode.Rest, still = m == Mode.Hold || m == Mode.Rest;
            if (m == Mode.Off) { if (wasStill) Move(n, true); }
            else if (still && !wasStill) Move(n, false);
            else if (!still && wasStill) Move(n, true);
            if (m == Mode.Hold) Aim(n); else Unfreeze(n);
            if (Plugin.BrainLog.Value && (m != was))
                Plugin.Log.LogInfo("Brain: " + n.Owner.name + " " + was + " -> " + m + " (" + why + ", " + n.Dist.ToString("0") + " m" + (n.Ranged ? ", " + n.Kind : "") + ")");
        }

        // Movement FSM: Animal_Run (run animation + forward velocity) or Animal_Idle (idle animation, velocity zero), only while the Attack
        // FSM is in its chase state (during a burst / swing the Attack FSM re-sends Animal_Run on its way back; see BeforeSendEvent).
        private static void Move(Npc n, bool run)
        {
            if (n.Movement == null || n.Attack == null || n.Attack.Fsm == null) return;
            string s = n.Attack.Fsm.ActiveStateName;
            if (s != "trigger" && s != "run") return;
            n.Movement.SendEvent(run ? "Animal_Run" : "Animal_Idle");
        }

        // A holding gunman keeps the gun up: the Movement FSM's shooting animation (attack 2 / attack 4: a 0.2-0.6 s clip that starts with the gun
        // raised) is put on its first frame and the Animator frozen there. The burst plays it from the start again (Unfreeze in Tick), and after
        // the burst the Attack FSM's Animal_Run comes back as Animal_Idle (BeforeSendEvent) followed by this pose again.
        private static void Aim(Npc n)
        {
            if (!Plugin.AimPose.Value || n.Anim == null || string.IsNullOrEmpty(n.AimState)) return;
            try
            {
                n.Anim.Play(n.AimState, 0, 0f);
                n.Anim.speed = 0f;
                n.Frozen = true;
            }
            catch (Exception) { n.AimState = null; }
        }

        private static void Unfreeze(Npc n)
        {
            if (!n.Frozen) return;
            n.Frozen = false;
            if (n.Anim != null) n.Anim.speed = 1f;
        }

        private static void OnStuck(Npc n)
        {
            float now = Time.time;
            if (n.Mode == Mode.Hold || n.Mode == Mode.Rest || n.Mode == Mode.BackUp || n.Mode == Mode.Off) return;
            if (now - n.FirstStuck > 20f) { n.FirstStuck = now; n.Stucks = 0; }
            n.Stucks++;
            var target = n.Target != null ? n.Target.Value : null;
            if (n.Ranged && target != null && n.Dist <= Tracers.RangeOf(n.Kind) && LineOfSight(n, target, target.transform.position))
            {
                n.NextRecheck = now + Mathf.Max(0.1f, Plugin.HoldRecheckMax.Value);
                SetMode(n, Mode.Hold, "stuck, shoots from here");
                return;
            }
            if (n.Stucks >= Mathf.Max(1, Plugin.StuckGiveUpCount.Value))
            {
                n.Stucks = 0;
                n.ModeUntil = now + 3f;
                SetMode(n, Mode.Rest, "stuck for good, rests");
                return;
            }
            float yaw = n.T.eulerAngles.y;
            n.BlockedYaw = yaw; n.BlockedUntil = now + Mathf.Max(0f, Plugin.StuckMemorySeconds.Value);
            n.ModeUntil = now + Mathf.Max(0.1f, Plugin.StuckBackupSeconds.Value);
            SetMode(n, Mode.BackUp, "stuck " + n.Stucks + "x, backs up");
        }

        // ---------- NPC registry ----------
        private static Npc Get(GameObject owner)
        {
            Npc n;
            return owner != null && _npcs.TryGetValue(owner.GetInstanceID(), out n) ? n : null;
        }

        // the first Movement SetVelocity of an NPC registers it (every ground NPC with a target runs that action every frame)
        private static Npc Of(GameObject owner)
        {
            if (owner == null) return null;
            int id = owner.GetInstanceID();
            Npc n;
            if (_npcs.TryGetValue(id, out n)) return n;
            if (_ignored.Contains(id)) return null;
            n = Make(owner);
            if (n == null) { _ignored.Add(id); return null; }
            _npcs[id] = n;
            return n;
        }

        private static Npc Make(GameObject owner)
        {
            if (owner.transform.parent != null) return null;         // seated in a car (Apocapatrol), or a part of something
            var rb = owner.GetComponent<Rigidbody>();
            if (rb == null || !rb.useGravity) return null;         // flyers (bats, wasps, Terror of the Night) have gravity off
            PlayMakerFSM attack = null, movement = null, detection = null, unstuck = null;
            foreach (var f in owner.GetComponents<PlayMakerFSM>())
            {
                if (f == null) continue;
                switch (f.FsmName)
                {
                    case "Attack": attack = f; break;
                    case "Movement": movement = f; break;
                    case "Detection": detection = f; break;
                    case "Unstuck": unstuck = f; break;
                }
            }
            if (attack == null || movement == null || detection == null || unstuck == null) return null;
            if (detection.Fsm == null || !detection.Fsm.Initialized) return null;    // try again next frame (not cached as ignored)
            var target = detection.FsmVariables.FindFsmGameObject("detectedObj");
            if (target == null) return null;
            var n = new Npc { Owner = owner, T = owner.transform, Rb = rb, Col = owner.GetComponent<Collider>(), Attack = attack, Movement = movement, Target = target };
            Tracers.Kind kind;
            n.Ranged = Tracers.GunKindOf(owner, out kind);
            n.Kind = kind;
            if (n.Ranged)
            {
                n.Anim = owner.GetComponentInChildren<Animator>(true);
                try
                {
                    var mf = movement.Fsm;
                    if (mf != null && mf.States != null)
                        foreach (var st in mf.States)
                        {
                            if (st == null || st.Name != "AttackRanged" || st.Actions == null) continue;
                            foreach (var a in st.Actions)
                            {
                                var ap = a as AnimatorPlay;
                                if (ap != null && ap.stateName != null && !string.IsNullOrEmpty(ap.stateName.Value)) { n.AimState = ap.stateName.Value; break; }
                            }
                        }
                }
                catch (Exception e) { Plugin.Verbose("Brain: no aim pose for " + owner.name + ": " + e.Message); }
            }
            n.Stagger = (_created++ % 10) * 0.013f;
            n.NextTick = Time.time + n.Stagger;
            return n;
        }

        private static Npc NpcOf(Fsm fsm, string fsmName)
        {
            if (fsm == null || fsm.Name != fsmName) return null;
            var n = Get(fsm.GameObject);
            return n != null && n.Mode != Mode.Off && On ? n : null;
        }

        // ---------- hooks ----------
        // SetVelocity.DoSetVelocity (Movement FSM): the pedal
        public static bool BeforeSetVelocity(SetVelocity __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Movement" || !On) return true;
                var n = Of(fsm.GameObject);
                if (n == null || n.Mode == Mode.Off) return true;
                float z = __instance.z != null && !__instance.z.IsNone ? __instance.z.Value : (__instance.vector != null && !__instance.vector.IsNone ? __instance.vector.Value.z : 0f);
                if (z <= 0f) return true;      // the Idle / attack states' "stop": vanilla
                if (n.Rb == null) return true;
                float speed = n.Mode == Mode.BackUp ? -Mathf.Min(z, 2.5f) : (n.Mode == Mode.Hold || n.Mode == Mode.Rest) ? 0f : z;
                Vector3 v = n.T.forward * speed;
                v.y = n.Rb.velocity.y;
                n.Rb.velocity = v;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // Rotate.DoRotate: the Rotate FSM's random yaw and the Attack FSM's run / rotate / turn states
        public static bool BeforeRotate(Rotate __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null) return true;
                string name = fsm.Name;
                if (name != "Rotate" && name != "Attack") return true;
                return NpcOf(fsm, name) == null;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // Raycast.DoRaycast: the Attack FSM's two 1.2 m bumper rays report "clear" (the feelers see 3-4 m)
        public static bool BeforeRaycast(Raycast __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || __instance.repeatInterval == null || __instance.repeatInterval.Value == 0) return true;
                var n = NpcOf(fsm, "Attack");
                if (n == null) return true;
                if (__instance.storeDidHit != null && !__instance.storeDidHit.IsNone) __instance.storeDidHit.Value = false;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // LookAt.DoLookAt: the Attack FSM's instant snap of the body toward the target (burst state) -> our turn rate instead
        public static bool BeforeLookAt(LookAt __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack") return true;
                if (__instance.gameObject == null || __instance.gameObject.OwnerOption != OwnerDefaultOption.UseOwner) return true;   // the sensor children: vanilla
                return NpcOf(fsm, "Attack") == null;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // SendEvent.OnEnter: Attack's Animal_Run while holding -> Animal_Idle; Unstuck's Animal_rotateRandom -> our stuck handling
        public static bool BeforeSendEvent(SendEvent __instance)
        {
            try
            {
                if (__instance.sendEvent == null) return true;
                string ev = __instance.sendEvent.Name;
                if (ev == "Animal_Run")
                {
                    var n = NpcOf(__instance.Fsm, "Attack");
                    if (n == null || (n.Mode != Mode.Hold && n.Mode != Mode.Rest)) return true;
                    if (n.Movement != null) n.Movement.SendEvent("Animal_Idle");
                    if (n.Mode == Mode.Hold) Aim(n);
                    __instance.Finish();
                    return false;
                }
                if (ev == "Animal_rotateRandom")
                {
                    var n = NpcOf(__instance.Fsm, "Unstuck");
                    if (n == null) return true;
                    OnStuck(n);
                    __instance.Finish();
                    return false;
                }
                return true;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // AddForce.DoAddForce (Unstuck): no hop
        public static bool BeforeAddForce(AddForce __instance)
        {
            try { return NpcOf(__instance.Fsm, "Unstuck") == null; }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        internal static string Status() { return _npcs.Count + " NPCs, " + _active + " engaged, tick " + _interval + " s"; }
    }
}

using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Micosmo.SensorToolkit.PlayMaker;
using UnityEngine;

namespace Apocaraiders
{
    // Visible bullets for every gun-wielding human (raiders, Coyotes, traders' guards ...).
    //
    // Vanilla (asset dump 2026-10-01, FSM "Damage Ranged", same on every shooter): state trigger = sound + muzzle flash, aim the
    // child AttackRaycast_Ranged (RaySensor, Length 80, DetectsOn layers 6+10, ObstructedBy 0/8/9/11/14) at detectedObj + a random
    // {offset}, then SensorGetDetectionRayHit: target reached -> state attack (CreateObject RangedHit_Effect at {hitPoint},
    // SetFsmFloat <target>/Bodypart.Damage = damage, SendEvent Damage to the target, Wait rpm) else -> shoot (Wait rpm, nothing).
    //
    // Here: a Harmony prefix on SensorGetDetectionRayHit.OnEnter, only inside "Damage Ranged" of an owner holding a gun or a crossbow,
    // fires a projectile from the gun's fire_effect point toward the same aimed point and sends the FSM down the miss branch, so the
    // burst rhythm stays vanilla. The projectile reproduces the attack state when it reaches the target. Monsters with a
    // "Damage Ranged" FSM (no gun model) keep the vanilla path.
    //
    // Cost: projectiles are structs in one list; each one costs one short RaycastNonAlloc per frame (the distance it moved); all lines
    // are one camera-facing mesh = one draw call with an unlit material (Sprites/Default: no lighting, same look day and night).
    // Above [Tracers] MaxTracers live projectiles a shot is resolved at once (hitscan) instead, so no damage is ever lost.
    internal static class Tracers
    {
        internal enum Kind { Pistol, Smg, Rifle, Sniper, Shotgun, Crossbow }

        private struct Shot
        {
            public Vector3 Pos, Dir, Start;
            public float Speed, Range, Damage, Travelled;
            public GameObject Target, ShooterRoot;
            public GameObject Impact;
            public string EventName;
            public bool Bolt, Shotgun, Alive;
            public int Layers, DetectLayers;
            public PlayerGun Gun;            // non-null: the player's shot (first collider it meets is hit, like the vanilla camera ray)
            public GameObject Player;
            public GameObject Head;          // the target's head object (own Bodypart FSM), for head hits
        }

        // A first-person gun under PlayerCamera/WeaponsArm/Parent/<gun>: its Attack FSM, the Raycast it fires and the states that
        // handle a hit (getLayer / actorHitSound / hit), replayed when the bullet lands.
        private sealed class PlayerGun
        {
            public Fsm Fsm;
            public Kind Kind;
            public Transform Muzzle;
            public int Pellets = 1;
            public float PelletSpread;
            public FsmFloat Damage;          // the hit state's Bodypart.Damage value (per bullet / pellet)
            public int Layers;
            public FsmStateAction[] GetLayer, ActorHit, Hit;
        }

        private sealed class ShooterInfo
        {
            public Transform Muzzle, Weapon;
            public Kind Kind;
            public bool IsGun;
            public int ObstructLayers = 19201, DetectLayers = 1088;
            public GameObject Impact;
            public FsmFloat DamageVar;      // the attack state's SetFsmFloat.setValue (usually {Damage Value})
            public float DamageLiteral;
            public string EventName = "Damage";
            public string BodypartFsm = "Bodypart", BodypartVar = "Damage";
        }

        private static readonly List<Shot> _shots = new List<Shot>(256);
        private static readonly Dictionary<int, ShooterInfo> _shooters = new Dictionary<int, ShooterInfo>();
        private static readonly RaycastHit[] _hits = new RaycastHit[16];
        private static readonly List<KeyValuePair<Rigidbody, Vector3>> _pushes = new List<KeyValuePair<Rigidbody, Vector3>>();
        private static readonly List<KeyValuePair<GameObject, Vector3>> _popped = new List<KeyValuePair<GameObject, Vector3>>();
        private static int _poppedFrame;

        // ---------- the hook ----------
        // Harmony prefix on Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit.OnEnter. false = vanilla skipped.
        public static bool BeforeRayHit(SensorGetDetectionRayHit __instance)
        {
            try
            {
                if (!Plugin.TracersEnabled.Value) return true;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Damage Ranged") return true;
                var owner = fsm.GameObject;
                if (owner == null) return true;
                var target = __instance.targetObject != null ? __instance.targetObject.Value : null;
                if (target == null) return true;
                var info = Info(fsm, owner);
                if (!info.IsGun) return true;

                // The vanilla target is whatever collider-object the sensor found nearest - for the player that is the "head" child
                // (its own Bodypart FSM), 3 cm nearer to an NPC's eye-level sensor than the body pivot. The bullet needs the whole
                // body: resolve to the root that carries the capsule, remember the head for head hits, and aim at the body centre
                // ([Tracers] NpcAimAtBody) instead of the head so the vanilla jitter lands on the body, not above it.
                GameObject head = null;
                var root = BodyRootOf(target);
                if (root != null && root != target) { head = target; target = root; }
                else if (root != null) head = HeadOf(root);
                var offsetVar = fsm.Variables.GetFsmVector3("offset");
                Vector3 jitter = offsetVar != null ? offsetVar.Value : Vector3.zero;
                if (Plugin.AimEnabled.Value)    // [NpcAim] SpreadPer5m: the jitter grows with distance
                    jitter *= Aim.SpreadFactor(Vector3.Distance(owner.transform.position, target.transform.position));
                Vector3 aim;
                var cap0 = root != null ? PlayerCapsule(root) : null;
                if (cap0 != null && Plugin.NpcAimAtBody.Value) aim = cap0.transform.TransformPoint(cap0.center) + jitter;
                else aim = (head != null ? head : target).transform.position + jitter;
                Transform muzzle = MuzzleOf(info, owner);
                Vector3 from = muzzle != null ? muzzle.position : fsm.GetOwnerDefaultTarget(__instance.gameObject).transform.position;
                Vector3 dir = aim - from;
                if (dir.sqrMagnitude < 1e-4f) dir = owner.transform.forward;
                dir.Normalize();

                float damage = info.DamageVar != null ? info.DamageVar.Value : info.DamageLiteral;
                int pellets = info.Kind == Kind.Shotgun ? Mathf.Clamp(Plugin.ShotgunPellets.Value, 1, 8) : 1;
                float spread = info.Kind == Kind.Shotgun ? Plugin.ShotgunPelletSpread.Value : 0f;
                for (int i = 0; i < pellets; i++)
                {
                    Vector3 d = dir;
                    if (spread > 0f && pellets > 1)
                        d = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, spread), Vector3.Cross(dir, UnityEngine.Random.onUnitSphere).normalized) * dir;
                    var s = new Shot
                    {
                        Pos = from, Start = from, Dir = d,
                        Speed = info.Kind == Kind.Crossbow ? Plugin.BoltSpeed.Value : Plugin.BulletSpeed.Value,
                        Range = RangeOf(info.Kind),
                        Damage = damage / pellets * (info.Kind == Kind.Shotgun ? Mathf.Max(0f, Plugin.NpcShotgunDamage.Value) : 1f),
                        Target = target,
                        Head = head,
                        ShooterRoot = owner.transform.root.gameObject,
                        Impact = info.Impact,
                        EventName = info.EventName,
                        Bolt = info.Kind == Kind.Crossbow,
                        Shotgun = info.Kind == Kind.Shotgun,
                        Alive = true,
                        Layers = info.ObstructLayers | info.DetectLayers,
                        DetectLayers = info.DetectLayers,
                    };
                    if (_shots.Count >= Plugin.MaxTracers.Value) { s.Speed = s.Range * 2f; Step(ref s, 1f); }   // over the cap: hitscan
                    else _shots.Add(s);
                }

                fsm.Event(__instance.isNotIntersectedEvent);    // vanilla goes down its miss branch: Wait rpm, next shot
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Tracers: " + e);
                return true;
            }
        }

        public static void OnSceneLoaded()
        {
            _shots.Clear();
            _shooters.Clear();
            Aim.OnSceneLoaded();
            _guns.Clear();
            _notGun.Clear();
            _heads.Clear();
            _player = null;
            _pushes.Clear();
            _popped.Clear();
        }

        // ---------- the player's guns ----------
        private static readonly Dictionary<Fsm, PlayerGun> _guns = new Dictionary<Fsm, PlayerGun>();
        private static readonly Dictionary<Fsm, bool> _notGun = new Dictionary<Fsm, bool>();
        private static GameObject _player;

        // Harmony prefix on HutongGames.PlayMaker.Actions.Raycast.OnEnter. Only the "fire" Raycast of a first-person gun's Attack FSM
        // (an FSM named Attack whose GameObject also has a Reload FSM; melee weapons have none). false = vanilla skipped.
        public static bool BeforeRaycast(Raycast __instance)
        {
            try
            {
                if (!Plugin.TracersEnabled.Value || !Plugin.PlayerTracers.Value) return true;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || __instance.repeatInterval == null || __instance.repeatInterval.Value != 0) return true;
                bool no;
                if (_notGun.TryGetValue(fsm, out no)) return true;
                PlayerGun gun;
                if (!_guns.TryGetValue(fsm, out gun))
                {
                    gun = MakeGun(fsm, __instance);
                    if (gun == null) { _notGun[fsm] = true; return true; }
                    _guns[fsm] = gun;
                }

                var from = fsm.GetOwnerDefaultTarget(__instance.fromGameObject);
                if (from == null) return true;
                Transform ft = from.transform;
                Vector3 origin = ft.position + (__instance.fromPosition != null && !__instance.fromPosition.IsNone ? __instance.fromPosition.Value : Vector3.zero);
                Vector3 local = __instance.direction != null && !__instance.direction.IsNone ? __instance.direction.Value : Vector3.forward;
                float range = RangeOf(gun.Kind);
                if (_player == null) _player = GameObject.Find("Player");

                Vector3 muzzle = gun.Muzzle != null && gun.Muzzle.gameObject.activeInHierarchy ? gun.Muzzle.position
                               : origin + ft.forward * 0.4f - ft.up * 0.15f;
                for (int p = 0; p < gun.Pellets; p++)
                {
                    Vector3 l = local;
                    if (p > 0) l = new Vector3(UnityEngine.Random.Range(-gun.PelletSpread, gun.PelletSpread), UnityEngine.Random.Range(-gun.PelletSpread, gun.PelletSpread), 1f);
                    Vector3 dir = __instance.space == Space.Self ? ft.TransformDirection(l) : l;
                    dir.Normalize();
                    // aim where the camera ray points, fly from the muzzle
                    RaycastHit aimHit;
                    Vector3 aim = Physics.Raycast(origin, dir, out aimHit, range, gun.Layers, QueryTriggerInteraction.Ignore) && !Ignored(aimHit.collider.transform, ft.root, _player)
                                  ? aimHit.point : origin + dir * range;
                    Vector3 d = aim - muzzle;
                    if (d.sqrMagnitude < 0.01f) d = dir;
                    var s = new Shot
                    {
                        Pos = muzzle, Start = muzzle, Dir = d.normalized,
                        Speed = gun.Kind == Kind.Crossbow ? Plugin.BoltSpeed.Value : Plugin.BulletSpeed.Value,
                        Range = range, Bolt = gun.Kind == Kind.Crossbow, Shotgun = gun.Kind == Kind.Shotgun, Alive = true,
                        Layers = gun.Layers, Gun = gun, Player = _player,
                        ShooterRoot = ft.root.gameObject,
                    };
                    if (_shots.Count >= Plugin.MaxTracers.Value) { s.Speed = s.Range * 2f; Step(ref s, 1f); }
                    else _shots.Add(s);
                }
                __instance.Finish();         // vanilla: no hit -> FINISHED -> wait -> next shot, same rhythm
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Tracers (player): " + e);
                return true;
            }
        }

        private static PlayerGun MakeGun(Fsm fsm, Raycast ray)
        {
            var go = fsm.GameObject;
            if (go == null) return null;
            bool reload = false;
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Reload") { reload = true; break; }
            if (!reload) return null;
            var hit = fsm.GetState("hit");
            if (hit == null) return null;
            var gun = new PlayerGun { Fsm = fsm, Hit = hit.Actions };
            foreach (var a in gun.Hit) { var sf = a as SetFsmFloat; if (sf != null && sf.fsmName != null && sf.fsmName.Value == "Bodypart") gun.Damage = sf.setValue; }
            var gl = fsm.GetState("getLayer"); if (gl != null) gun.GetLayer = gl.Actions;
            var ah = fsm.GetState("actorHitSound"); if (ah != null) gun.ActorHit = ah.Actions;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("muzzle_flesh_effect", StringComparison.Ordinal) || t.name.StartsWith("muzzle_flash", StringComparison.Ordinal)) { gun.Muzzle = t; break; }
            // layers: the Raycast's own mask
            int mask = 0;
            if (ray.layerMask != null) foreach (var l in ray.layerMask) if (l != null) mask |= 1 << l.Value;
            if (mask == 0) mask = ~0;
            if (ray.invertMask != null && ray.invertMask.Value) mask = ~mask;
            gun.Layers = mask;
            // shotguns: a "pellets" counter; the hit state loops until pellets > N
            var pv = fsm.Variables.GetFsmInt("pellets");
            bool shotgun = pv != null && !string.IsNullOrEmpty(pv.Name);
            if (shotgun)
            {
                gun.Pellets = 9;
                foreach (var a in gun.Hit) { var ic = a as IntCompare; if (ic != null && ic.integer2 != null) gun.Pellets = Mathf.Clamp(ic.integer2.Value + 1, 1, 32); }
                var fire = fsm.GetState("fire");
                gun.PelletSpread = 0.02f;
                if (fire != null) foreach (var a in fire.Actions) { var rf = a as RandomFloat; if (rf != null && rf.max != null) { gun.PelletSpread = Mathf.Abs(rf.max.Value); break; } }
            }
            gun.Kind = shotgun ? Kind.Shotgun : Classify(go.name);
            if (gun.Kind == Kind.Shotgun && !shotgun) gun.Kind = Kind.Rifle;      // a "rochester" without pellets is not a shotgun
            Plugin.Verbose("Tracers: player gun " + go.name + " = " + gun.Kind + (shotgun ? ", " + gun.Pellets + " pellets" : "") + (gun.Muzzle != null ? "" : ", no muzzle point"));
            return gun;
        }

        private static bool Ignored(Transform t, Transform shooterRoot, GameObject player)
        {
            if (shooterRoot != null && t.IsChildOf(shooterRoot)) return true;
            if (player != null && t.IsChildOf(player.transform.root)) return true;     // the player and, when driving, their car
            return false;
        }

        // the gun's vanilla hit path, for the collider the bullet met: set its hitObj/hitPoint/hitNormal, then getLayer or
        // actorHitSound (which impact sound), then the hit state's CreateObject / SetFsmFloat (damage x falloff) / SendEvent actions
        private static void PlayerHit(ref Shot s, RaycastHit h, float falloff)
        {
            var gun = s.Gun;
            var fsm = gun.Fsm;
            if (fsm == null) return;
            var go = h.collider.gameObject;
            var vo = fsm.Variables.GetFsmGameObject("hitObj"); if (vo != null) vo.Value = go;
            var vp = fsm.Variables.GetFsmVector3("hitPoint"); if (vp != null) vp.Value = h.point;
            var vn = fsm.Variables.GetFsmVector3("hitNormal"); if (vn != null) vn.Value = h.normal;
            Replay(fsm, go.layer == 10 && gun.ActorHit != null ? gun.ActorHit : gun.GetLayer, falloff);
            Replay(fsm, gun.Hit, falloff);
        }

        private static void Replay(Fsm fsm, FsmStateAction[] actions, float falloff)
        {
            if (actions == null) return;
            foreach (var a in actions)
            {
                try
                {
                    var co = a as CreateObject;
                    if (co != null)
                    {
                        var prefab = co.gameObject != null ? co.gameObject.Value : null;
                        if (prefab == null) continue;
                        var sp = co.spawnPoint != null ? co.spawnPoint.Value : null;
                        Vector3 pos; Quaternion rot;
                        bool hasPos = co.position != null && !co.position.IsNone, hasRot = co.rotation != null && !co.rotation.IsNone;
                        if (sp != null)
                        {
                            pos = sp.transform.position + (hasPos ? co.position.Value : Vector3.zero);
                            rot = hasRot ? Quaternion.Euler(co.rotation.Value) : sp.transform.rotation;
                        }
                        else
                        {
                            pos = hasPos ? co.position.Value : Vector3.zero;
                            rot = hasRot ? Quaternion.Euler(co.rotation.Value) : prefab.transform.rotation;
                        }
                        var obj = UnityEngine.Object.Instantiate(prefab, pos, rot);
                        var par = co.parent != null ? co.parent.Value : null;
                        if (par != null) obj.transform.parent = par.transform;
                        continue;
                    }
                    var sf = a as SetFsmFloat;
                    if (sf != null)
                    {
                        var tgt = fsm.GetOwnerDefaultTarget(sf.gameObject);
                        if (tgt == null || sf.setValue == null) continue;
                        string fn = sf.fsmName != null ? sf.fsmName.Value : "", vn = sf.variableName != null ? sf.variableName.Value : "";
                        foreach (var f in tgt.GetComponents<PlayMakerFSM>())
                            if (f.FsmName == fn) { var v = f.FsmVariables.GetFsmFloat(vn); if (v != null) v.Value = sf.setValue.Value * falloff; break; }
                        continue;
                    }
                    var sg = a as SetFsmGameObject;
                    if (sg != null)
                    {
                        var tgt = fsm.GetOwnerDefaultTarget(sg.gameObject);
                        if (tgt == null || sg.setValue == null) continue;
                        string fn = sg.fsmName != null ? sg.fsmName.Value : "", vn = sg.variableName != null ? sg.variableName.Value : "";
                        foreach (var f in tgt.GetComponents<PlayMakerFSM>())
                            if (f.FsmName == fn) { var v = f.FsmVariables.GetFsmGameObject(vn); if (v != null) v.Value = sg.setValue.Value; break; }
                        continue;
                    }
                    var se = a as SendEvent;
                    if (se != null && se.sendEvent != null) { fsm.Event(se.eventTarget, se.sendEvent); continue; }
                }
                catch (Exception e) { Plugin.Verbose("Tracers: replay " + a.GetType().Name + " failed: " + e.Message); }
            }
        }

        // ---------- shooter info (once per FSM owner) ----------
        private static ShooterInfo Info(Fsm fsm, GameObject owner)
        {
            ShooterInfo info;
            int id = owner.GetInstanceID();
            if (_shooters.TryGetValue(id, out info) && (info.Weapon == null || info.Weapon.gameObject.activeInHierarchy)) return info;
            info = new ShooterInfo();
            _shooters[id] = info;

            // the weapon in the hand: a model with a fire_effect child (guns) or an active "crossbow"
            foreach (var t in owner.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "fire_effect" && t.parent != null && t.parent.name != "fire_effect" && t.parent.gameObject.activeInHierarchy)
                { info.Weapon = t.parent; info.Muzzle = t; break; }
            }
            if (info.Weapon == null)
                foreach (var t in owner.GetComponentsInChildren<Transform>(false))
                    if (t.name.StartsWith("crossbow", StringComparison.OrdinalIgnoreCase) && t.GetComponent<Renderer>() != null)
                    { info.Weapon = t; break; }
            if (info.Weapon == null) return info;    // a monster: vanilla
            info.IsGun = true;
            info.Kind = Classify(info.Weapon.name);

            // the vanilla attack state: impact prefab, damage, event
            var st = fsm.GetState("attack");
            if (st != null && st.Actions != null)
                foreach (var a in st.Actions)
                {
                    var co = a as CreateObject;
                    if (co != null && co.gameObject != null && co.gameObject.Value != null) info.Impact = co.gameObject.Value;
                    var sf = a as SetFsmFloat;
                    if (sf != null && sf.setValue != null)
                    {
                        info.DamageVar = sf.setValue;
                        if (sf.fsmName != null && !string.IsNullOrEmpty(sf.fsmName.Value)) info.BodypartFsm = sf.fsmName.Value;
                        if (sf.variableName != null && !string.IsNullOrEmpty(sf.variableName.Value)) info.BodypartVar = sf.variableName.Value;
                    }
                    var se = a as SendEvent;
                    if (se != null && se.sendEvent != null && !string.IsNullOrEmpty(se.sendEvent.Name)) info.EventName = se.sendEvent.Name;
                }
            if (info.DamageVar == null) info.DamageLiteral = -7f;

            // the vanilla ray's layers
            var ray = owner.transform.Find("AttackRaycast_Ranged");
            if (ray != null)
            {
                var rs = ray.GetComponent<Micosmo.SensorToolkit.RaySensor>();
                if (rs != null) { info.ObstructLayers = rs.ObstructedByLayers.value; info.DetectLayers = rs.DetectsOnLayers.value; }
            }
            Plugin.Verbose("Tracers: " + owner.name + " fires " + info.Kind + " (" + info.Weapon.name + "), damage " +
                           (info.DamageVar != null ? info.DamageVar.Value : info.DamageLiteral) + ", impact " + (info.Impact != null ? info.Impact.name : "none"));
            return info;
        }

        private static Transform MuzzleOf(ShooterInfo info, GameObject owner)
        {
            if (info.Muzzle != null) return info.Muzzle;
            return info.Weapon;
        }

        internal static Kind Classify(string weapon)
        {
            string n = weapon.ToLowerInvariant();
            if (n.Contains("crossbow")) return Kind.Crossbow;
            if (n.Contains("shotgun") || n.Contains("slamfire") || n.Contains("slamberg") || n.Contains("rochester")) return Kind.Shotgun;
            if (n.Contains("scoped") || n.Contains("sniper") || n.Contains("redmark")) return Kind.Sniper;
            if (n.Contains("smg") || n.Contains("borz")) return Kind.Smg;
            if (n.Contains("pistol") || n.Contains("revolver") || n.Contains("folk_17")) return Kind.Pistol;
            return Kind.Rifle;
        }

        // The gun an NPC holds, for the Aim pacing (cached like Info; finds the Damage Ranged FSM itself).
        internal static bool GunKindOf(GameObject owner, out Kind kind)
        {
            kind = Kind.Rifle;
            ShooterInfo info;
            if (!_shooters.TryGetValue(owner.GetInstanceID(), out info) || (info.Weapon != null && !info.Weapon.gameObject.activeInHierarchy))
            {
                Fsm dr = null;
                foreach (var f in owner.GetComponents<PlayMakerFSM>())
                    if (f != null && f.FsmName == "Damage Ranged") { dr = f.Fsm; break; }
                if (dr == null) return false;
                info = Info(dr, owner);
            }
            if (!info.IsGun) return false;
            kind = info.Kind;
            return true;
        }

        // Damage multiplier by distance flown. Guns: 1 at the muzzle, linear to 0 at the range. Shotguns: full damage until
        // [Tracers] ShotgunFullDamageUntil % of the range, then linear to 0 at the range (so at 50 % a shotgun does 1x where a gun does 0.5x).
        private static float Falloff(ref Shot s, float dist)
        {
            float x = dist / s.Range;
            if (s.Shotgun)
            {
                float k = Mathf.Clamp(Plugin.ShotgunFullDamageUntil.Value, 0f, 99f) / 100f;
                if (x <= k) return 1f;
                return Mathf.Clamp01((1f - x) / (1f - k));
            }
            return Mathf.Clamp01(1f - x);
        }

        internal static float RangeOf(Kind k)
        {
            switch (k)
            {
                case Kind.Pistol: return Mathf.Max(1f, Plugin.PistolRange.Value);
                case Kind.Smg: return Mathf.Max(1f, Plugin.SmgRange.Value);
                case Kind.Sniper: return Mathf.Max(1f, Plugin.SniperRange.Value);
                case Kind.Shotgun: return Mathf.Max(1f, Plugin.ShotgunRange.Value);
                case Kind.Crossbow: return Mathf.Max(1f, Plugin.CrossbowRange.Value);
                default: return Mathf.Max(1f, Plugin.RifleRange.Value);
            }
        }

        // ---------- simulation ----------
        public static void Tick()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) { Draw(); return; }   // paused: keep drawing, don't move
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var s = _shots[i];
                Step(ref s, dt);
                if (s.Alive) _shots[i] = s; else _shots.RemoveAt(i);
            }
            if (_pushes.Count > 0)
            {
                foreach (var p in _pushes) if (p.Key != null) p.Key.AddForce(p.Value, ForceMode.VelocityChange);
                _pushes.Clear();
            }
            if (_popped.Count > 0 && Time.frameCount >= _poppedFrame)
            {
                // CheckTag adds the Rigidbody a frame or two after de_Attach
                for (int i = _popped.Count - 1; i >= 0; i--)
                {
                    var go = _popped[i].Key;
                    var rb = go != null ? go.GetComponent<Rigidbody>() : null;
                    if (rb != null) { rb.AddForce(_popped[i].Value, ForceMode.VelocityChange); _popped.RemoveAt(i); }
                    else if (go == null || Time.frameCount > _poppedFrame + 30) _popped.RemoveAt(i);
                }
            }
            Draw();
        }

        private static void Step(ref Shot s, float dt)
        {
            float move = Mathf.Min(s.Speed * dt, s.Range - s.Travelled);
            if (move <= 0f) { s.Alive = false; return; }
            // NPC bullets have a thickness: the player's body is two capsules only 0.34-0.40 m wide (head at the camera), so a
            // hairline that visibly passes through your face misses the collider by centimetres. Player bullets stay a hairline.
            float radius = s.Gun == null ? Mathf.Max(0f, Plugin.NpcHitRadius.Value) : 0f;
            // NPC bullet at the player: the virtual hitbox decides the hit on the player, physics only the obstructions in front of it
            CapsuleCollider vcap = null;
            float vt = float.MaxValue; bool vhead = false;
            if (s.Gun == null && s.Target != null && (vcap = PlayerCapsule(s.Target)) != null)
            {
                if (!PlayerHit(s.Pos, s.Dir, move, radius, s.Target, vcap, out vt, out vhead)) vt = float.MaxValue;
            }
            int n = radius > 0f
                ? Physics.SphereCastNonAlloc(s.Pos, radius, s.Dir, _hits, move, s.Layers, QueryTriggerInteraction.Ignore)
                : Physics.RaycastNonAlloc(s.Pos, s.Dir, _hits, move, s.Layers, QueryTriggerInteraction.Ignore);
            if (n > 1) Array.Sort(_hits, 0, n, HitDistance.Instance);
            for (int k = 0; k < n; k++)
            {
                var h = _hits[k];
                var col = h.collider;
                if (col == null) continue;
                var tr = col.transform;
                if (vcap != null && tr.IsChildOf(s.Target.transform)) continue;      // the real capsules: the virtual hitbox handles the player
                if (vcap != null && h.distance > vt) break;                          // the player is hit before this obstruction
                if (s.Gun != null)
                {
                    if (Ignored(tr, s.ShooterRoot != null ? s.ShooterRoot.transform : null, s.Player)) continue;
                    float pd = s.Travelled + h.distance;
                    float pf = Falloff(ref s, pd);
                    PlayerHit(ref s, h, pf);
                    HitWorld(ref s, col, h.point, (s.Gun.Damage != null ? s.Gun.Damage.Value : 0f) * pf);   // vehicle part / metal plate rules
                    s.Pos = h.point; s.Travelled = pd; s.Alive = false;
                    return;
                }
                if (s.ShooterRoot != null && tr.IsChildOf(s.ShooterRoot.transform)) continue;        // own body / own car
                bool onTarget = s.Target != null && tr.IsChildOf(s.Target.transform);
                bool detectable = (s.DetectLayers & (1 << col.gameObject.layer)) != 0;
                if (!onTarget && detectable) continue;      // other creatures don't stop a vanilla shot either
                float dist = s.Travelled + h.distance;
                float falloff = Falloff(ref s, dist);
                if (onTarget) HitTarget(ref s, h.point, s.Damage * falloff, s.Target);
                else HitWorld(ref s, col, h.point, s.Damage * falloff);
                s.Pos = h.point;
                s.Travelled = dist;
                s.Alive = false;
                return;
            }
            if (vcap != null && vt < float.MaxValue)
            {
                float dist = s.Travelled + vt;
                float falloff = Falloff(ref s, dist);
                float mult = vhead ? Mathf.Max(0f, Plugin.HeadshotMultiplier.Value) : 1f;
                if (vhead) Plugin.Verbose("Tracers: headshot on " + s.Target.name + " at " + dist.ToString("0.0") + " m");
                HitTarget(ref s, s.Pos + s.Dir * vt, s.Damage * falloff * mult, vhead && s.Head != null ? s.Head : s.Target);
                s.Pos += s.Dir * vt;
                s.Travelled = dist;
                s.Alive = false;
                return;
            }
            s.Pos += s.Dir * move;
            s.Travelled += move;
            if (s.Travelled >= s.Range - 1e-3f) s.Alive = false;
        }

        // ---------- the player's hitbox for NPC bullets ----------
        // The game's player collider is two slim physics capsules (r 0.17 / 0.20) with the camera on their axis and a trigger sphere
        // for the head. Tracers aimed at the player use their own shapes instead, built each frame from the tallest real capsule so a
        // crouch is followed: a body capsule (feet to neck, [Tracers] PlayerBodyRadius) and a head sphere ([Tracers] PlayerHeadRadius)
        // at the top. A bullet hits whichever it reaches first, once.
        // the object that carries the body capsule: the target itself or its nearest ancestor (the player's "head" child -> Player)
        private static GameObject BodyRootOf(GameObject target)
        {
            for (var tr = target.transform; tr != null; tr = tr.parent)
                if (PlayerCapsule(tr.gameObject) != null) return tr.gameObject;
            return null;
        }

        private static readonly Dictionary<int, GameObject> _heads = new Dictionary<int, GameObject>();
        private static GameObject HeadOf(GameObject root)
        {
            GameObject h;
            int id = root.GetInstanceID();
            if (_heads.TryGetValue(id, out h) && h != null) return h;
            h = null;
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                if (tr != root.transform && tr.name == "head")
                    foreach (var f in tr.GetComponents<PlayMakerFSM>())
                        if (f.FsmName == "Bodypart") { h = tr.gameObject; break; }
            _heads[id] = h;
            return h;
        }

        private static CapsuleCollider PlayerCapsule(GameObject target)
        {
            CapsuleCollider best = null;
            foreach (var c in target.GetComponents<CapsuleCollider>())
                if (c.enabled && !c.isTrigger && (best == null || c.height > best.height)) best = c;
            return best;
        }

        // segment p0 + d*t (0..len) vs the virtual shapes; returns the nearest hit distance along the bullet
        private static bool PlayerHit(Vector3 p0, Vector3 d, float len, float thick, GameObject target, CapsuleCollider cap, out float t, out bool head)
        {
            t = 0f; head = false;
            var tr = cap.transform;
            Vector3 up = cap.direction == 1 ? tr.up : cap.direction == 0 ? tr.right : tr.forward;
            float sc = Mathf.Abs(cap.direction == 1 ? tr.lossyScale.y : cap.direction == 0 ? tr.lossyScale.x : tr.lossyScale.z);
            Vector3 center = tr.TransformPoint(cap.center);
            float half = Mathf.Max(0f, cap.height * sc * 0.5f);
            Vector3 feet = center - up * half, top = center + up * half;
            float headR = Mathf.Max(0.03f, Plugin.PlayerHeadRadius.Value), bodyR = Mathf.Max(0.03f, Plugin.PlayerBodyRadius.Value);
            Vector3 headC = top - up * headR;
            Vector3 neck = headC - up * headR;
            // body capsule: its rounded top ends exactly at the neck, so the head zone belongs to the head alone
            Vector3 bodyLo = feet + up * bodyR, bodyHi = neck - up * bodyR;
            if (Vector3.Dot(bodyHi - bodyLo, up) < 0f) bodyHi = bodyLo;         // crouched very low: a ball
            float th, tb;
            bool hh = RaySphere(p0, d, len, headC, headR + thick, out th);
            bool hb = SegCapsule(p0, d, len, bodyLo, bodyHi, bodyR + thick, out tb);
            if (!hh && !hb) return false;
            if (hh && (!hb || th <= tb)) { t = th; head = true; } else t = tb;
            return true;
        }

        private static bool RaySphere(Vector3 p0, Vector3 d, float len, Vector3 c, float r, out float t)
        {
            t = 0f;
            Vector3 m = p0 - c;
            float b = Vector3.Dot(m, d), cc = Vector3.Dot(m, m) - r * r;
            if (cc > 0f && b > 0f) return false;
            float disc = b * b - cc;
            if (disc < 0f) return false;
            t = -b - Mathf.Sqrt(disc);
            if (t < 0f) t = 0f;            // starts inside
            return t <= len;
        }

        // closest approach between the bullet segment and the capsule axis a..b (Ericson, Real-Time Collision Detection 5.1.9)
        private static bool SegCapsule(Vector3 p0, Vector3 d, float len, Vector3 a, Vector3 b, float r, out float t)
        {
            Vector3 p1 = p0 + d * len;
            Vector3 d1 = p1 - p0, d2 = b - a, rr = p0 - a;
            float A = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, rr);
            float s, u;
            if (A <= 1e-8f && e <= 1e-8f) { s = u = 0f; }
            else if (A <= 1e-8f) { s = 0f; u = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, rr);
                if (e <= 1e-8f) { u = 0f; s = Mathf.Clamp01(-c / A); }
                else
                {
                    float bb = Vector3.Dot(d1, d2), den = A * e - bb * bb;
                    s = den != 0f ? Mathf.Clamp01((bb * f - c * e) / den) : 0f;
                    u = (bb * s + f) / e;
                    if (u < 0f) { u = 0f; s = Mathf.Clamp01(-c / A); }
                    else if (u > 1f) { u = 1f; s = Mathf.Clamp01((bb - c) / A); }
                }
            }
            Vector3 c1 = p0 + d1 * s, c2 = a + d2 * u;
            if ((c1 - c2).sqrMagnitude > r * r) { t = 0f; return false; }
            // back up from the closest approach to the surface along the bullet (entry point), never before the segment start
            float along = s * len;
            float gap = Mathf.Sqrt(Mathf.Max(0f, r * r - (c1 - c2).sqrMagnitude));
            t = Mathf.Max(0f, along - gap);
            return true;
        }

        private sealed class HitDistance : IComparer<RaycastHit>
        {
            public static readonly HitDistance Instance = new HitDistance();
            public int Compare(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); }
        }

        // exactly the vanilla attack state: impact effect at the point, <target>/Bodypart.Damage = damage, SendEvent Damage to the target
        private static void HitTarget(ref Shot s, Vector3 point, float damage, GameObject target)
        {
            if (s.Impact != null) UnityEngine.Object.Instantiate(s.Impact, point, Quaternion.identity);
            if (target == null || Mathf.Abs(damage) < 0.01f) return;
            var fsms = target.GetComponents<PlayMakerFSM>();
            foreach (var f in fsms)
                if (f.FsmName == "Bodypart")
                {
                    var v = f.FsmVariables.GetFsmFloat("Damage");
                    if (v != null) v.Value = damage;
                }
            foreach (var f in fsms) f.SendEvent(s.EventName);
        }

        // obstruction: a vehicle part loses condition, a bolted metal plate may come off
        private static void HitWorld(ref Shot s, Collider col, Vector3 point, float damage)
        {
            Transform part = null;
            for (var t = col.transform; t != null; t = t.parent)
                if (t.CompareTag("vehPart")) { part = t; break; }
            if (part == null) return;

            string names = Plugin.MetalSheetNames.Value ?? "";
            foreach (var raw in names.Split(','))
            {
                var nm = raw.Trim();
                if (nm.Length == 0 || !part.name.StartsWith(nm, StringComparison.OrdinalIgnoreCase)) continue;
                if (UnityEngine.Random.Range(0f, 100f) < Plugin.MetalSheetPopChance.Value)
                {
                    foreach (var f in part.GetComponents<PlayMakerFSM>())
                        if (f.FsmName == "de_Attach") { f.SendEvent("de_Attach"); break; }
                    _popped.Add(new KeyValuePair<GameObject, Vector3>(part.gameObject, s.Dir * 3f + Vector3.up));
                    _poppedFrame = Time.frameCount + 2;
                    Plugin.Verbose("Tracers: " + part.name + " shot off");
                }
                return;
            }

            if (!Plugin.VehicleDamage.Value) return;
            float pct = Mathf.Abs(damage) / 10f;       // 10 damage = 1 % condition
            if (pct <= 0f) return;
            foreach (var f in part.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f.FsmName != "Condition" && f.FsmName != "Repair") continue;
                if (f.Fsm == null || !f.Fsm.Initialized) continue;
                if (OwningPart(f.transform) != part) continue;     // not a nested part's FSM
                var v = f.FsmVariables.GetFsmFloat("Condition");
                if (v != null) v.Value = Mathf.Max(0f, v.Value - pct);
            }
        }

        private static Transform OwningPart(Transform t)
        {
            for (; t != null; t = t.parent) if (t.CompareTag("vehPart")) return t;
            return null;
        }

        // ---------- drawing: one mesh, camera-facing quads ----------
        private static GameObject _drawGo;
        private static Mesh _mesh;
        private static Material _mat;
        private static readonly List<Vector3> _v = new List<Vector3>();
        private static readonly List<Color> _c = new List<Color>();
        private static readonly List<int> _t = new List<int>();

        private static void Draw()
        {
            if (_drawGo == null)
            {
                _drawGo = new GameObject("Apocaraiders.Tracers") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_drawGo);
                _mesh = new Mesh { name = "Tracers" };
                _mesh.MarkDynamic();
                _drawGo.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var mr = _drawGo.AddComponent<MeshRenderer>();
                var sh = Shader.Find("Sprites/Default");
                _mat = new Material(sh) { name = "TracerLine", renderQueue = 3100 };
                mr.sharedMaterial = _mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            float glow = Mathf.Max(0f, Plugin.TracerGlow.Value);
            _mat.color = new Color(glow, glow, glow, 1f);

            _v.Clear(); _c.Clear(); _t.Clear();
            var cam = Camera.main;
            if (cam != null && _shots.Count > 0)
            {
                Vector3 eye = cam.transform.position;
                Color tc = Plugin.TracerColor.Value, bc = Plugin.BoltColor.Value;
                float tw = Plugin.TracerWidth.Value, tl = Plugin.TracerLength.Value;
                float bw = Plugin.BoltWidth.Value, bl = Plugin.BoltLength.Value;
                foreach (var s in _shots)
                {
                    float len = Mathf.Min(s.Bolt ? bl : tl, s.Travelled);
                    if (len <= 0.01f) continue;
                    Vector3 head = s.Pos, tail = s.Pos - s.Dir * len;
                    Vector3 side = Vector3.Cross(s.Dir, (head - eye).normalized);
                    if (side.sqrMagnitude < 1e-6f) continue;
                    side = side.normalized * ((s.Bolt ? bw : tw) * 0.5f);
                    Color head_c = s.Bolt ? bc : tc, tail_c = head_c;
                    tail_c.a = s.Bolt ? head_c.a : 0f;           // tracers fade toward the tail, bolts are solid
                    int b = _v.Count;
                    _v.Add(tail - side); _v.Add(tail + side); _v.Add(head + side); _v.Add(head - side);
                    _c.Add(tail_c); _c.Add(tail_c); _c.Add(head_c); _c.Add(head_c);
                    _t.Add(b); _t.Add(b + 1); _t.Add(b + 2); _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
                }
            }
            _mesh.Clear();
            if (_v.Count > 0)
            {
                _mesh.SetVertices(_v);
                _mesh.SetColors(_c);
                _mesh.SetTriangles(_t, 0);
                _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            }
        }
    }
}

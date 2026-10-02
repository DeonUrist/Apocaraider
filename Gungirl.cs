using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Apocaraider
{
    // Gungirl = a Flexa raider wearing the female body (Models/flexa_female.gltf + .png).
    //
    // Spawning: every vanilla Flexa spawn goes through PlayMaker's CreateObject (camp/POI spawners); a Harmony postfix
    // turns [Gungirl] Chance % of them into Gungirls. Flexa's own Health FSM creates her corpse "Flexa_Dead" with
    // CreateObject on death -> if the dying Flexa is a Gungirl, so is the corpse.
    //
    // Save/load without any save file of our own: the game (Easy Save 3) restores every enemy and corpse by prefab +
    // name + Transform, and nothing in the game reads the name (checked: the only "Flexa" string in any FSM is the corpse's
    // mouse-over label). So a Gungirl is simply NAMED Gungirl: the hook renames the fresh instance "Flexa(Clone)" ->
    // "Gungirl(Clone)" before the game's spawner appends its itemNameID number ("Gungirl(Clone)281"), the corpse likewise
    // ("Gungirl_Dead(Clone)N"), and after a load the scan finds Gungirl* / Gungirl_Dead* roots and swaps their body again.
    // Versions before 0.6.0 marked her with a root scale x = y * (1 + 1/4096) instead; such Flexas from older saves are
    // renamed on sight (the scale is left alone - harmless).
    internal static class Gungirl
    {
        public const string Name = "Gungirl", CorpseName = "Gungirl_Dead";

        private static SkinModel _model;
        private static bool _modelTried;
        private static Texture2D _tex;
        private static bool _texTried;
        private static readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>();   // per bone layout
        private static readonly HashSet<Mesh> _ours = new HashSet<Mesh>();
        private static readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();   // by material name: per-instance copies share one
        private static readonly HashSet<int> _dressed = new HashSet<int>();     // root ids already dressed (a skinned prop never re-enters Apply)
        private static GameObject _prefab;
        private static float _nextScan, _burstUntil; private static bool _burstPending;

        // ---------- identity = the name ----------
        private static bool IsFlexa(string name) { return name == "Flexa" || name.StartsWith("Flexa(", StringComparison.Ordinal); }
        private static bool IsFlexaCorpse(string name) { return name.StartsWith("Flexa_Dead", StringComparison.Ordinal); }
        private static bool IsGungirlName(string name) { return name == Name || name.StartsWith(Name + "(", StringComparison.Ordinal); }
        private static bool IsGungirlCorpse(string name) { return name.StartsWith(CorpseName, StringComparison.Ordinal); }

        // "Flexa(Clone)281" -> "Gungirl(Clone)281", "Flexa_Dead(Clone)" -> "Gungirl_Dead(Clone)"
        private static void Rename(GameObject go)
        {
            string n = go.name;
            if (IsFlexaCorpse(n)) go.name = CorpseName + n.Substring("Flexa_Dead".Length);
            else if (IsFlexa(n)) go.name = Name + n.Substring("Flexa".Length);
        }

        // the pre-0.6.0 marker (root scale x = y * (1 + 1/4096)) - read only, to convert Gungirls from older saves
        private static bool IsOldMarked(Transform t)
        {
            if (t == null) return false;
            var s = t.localScale;
            if (Mathf.Abs(s.y) < 1e-6f) return false;
            float r = s.x / s.y;
            return r > 1.0001f && r < 1.0005f;
        }

        // ---------- public API for other mods (reflection-friendly) ----------
        // Turns a live Flexa (or a Flexa_Dead corpse) into a Gungirl. Returns false if the model could not be applied.
        public static bool MakeGungirl(GameObject root)
        {
            if (root == null) return false;
            Rename(root);
            return Apply(root, "api");
        }

        public static bool IsGungirl(GameObject root) { return root != null && (IsGungirlName(root.name) || IsGungirlCorpse(root.name)); }

        // ---------- hooks ----------
        // Harmony postfix on HutongGames.PlayMaker.Actions.CreateObject.OnEnter
        public static void AfterCreateObject(CreateObject __instance)
        {
            try
            {
                var store = __instance.storeObject;
                GameObject go = store != null && !store.IsNone ? store.Value : null;
                if (go == null) return;
                var prefabVar = __instance.gameObject;
                if (prefabVar == null || prefabVar.Value == null) return;
                string pn = Senses.PrefabOf(prefabVar.Value);      // cached per prefab asset: no name string per spawn

                if (IsFlexaCorpse(pn))
                {
                    var owner = __instance.Fsm != null ? __instance.Fsm.GameObject : null;
                    if (owner != null && IsGungirlName(owner.name))
                    {
                        Rename(go);                 // "Gungirl_Dead(Clone)"; the Health FSM appends the itemNameID number next
                        Apply(go, "corpse");
                    }
                }
                else if (pn == "Flexa")
                {
                    if (!Plugin.Enabled.Value) return;
                    if (UnityEngine.Random.Range(0, 100) >= Plugin.GungirlChance.Value) return;
                    Rename(go);                     // "Gungirl(Clone)"; the spawner appends the itemNameID number next
                    Apply(go, "spawn");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("CreateObject hook: " + e); }
        }

        public static void OnSceneLoaded()
        {
            _burstPending = true;      // the quick scans start when the player exists (the save is being restored then)
            _nextScan = 0f;
            _dressed.Clear();
        }

        public static void Tick()
        {
            try
            {
                var key = Plugin.SpawnKey.Value;
                if (key != Key.None && Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame) SpawnInFront();
            }
            catch (Exception e) { Plugin.Log.LogError("Spawn key: " + e); }

            try { Voice.Tick(); }
            catch (Exception e) { Plugin.Log.LogError("Voice: " + e); }

            float t = Time.unscaledTime;
            if (t < _nextScan) return;
            if (Nav.Player() == null) { _nextScan = t + 1f; return; }   // menu / loading: nobody to dress yet
            if (_burstPending) { _burstPending = false; _burstUntil = t + 45f; }
            // restored Gungirls and corpses appear during the load: scan then. Afterwards living ones are dressed when NPC detection registers
            // them (Registered); the slow scan stays only when NPC detection is off
            if (t >= _burstUntil && Senses.On) { _nextScan = t + 1f; return; }
            _nextScan = t + (t < _burstUntil ? 1f : 5f);
            try { Scan(); }
            catch (Exception e) { Plugin.Log.LogError("Scan: " + e); _nextScan = t + 10f; }
        }

        // Re-dress Gungirls restored from a save (by name), and convert Flexas carrying the pre-0.6.0 scale mark.
        private static void Scan()
        {
            foreach (var smr in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                var m = smr.sharedMesh;
                if (m == null || _ours.Contains(m)) continue;
                var bones = smr.bones;
                if (bones == null || bones.Length < 10) continue;     // props, not bodies (Apply ignores them too)
                Transform root = null; bool old = false;
                for (var p = smr.transform; p != null; p = p.parent)
                {
                    if (IsGungirlName(p.name) || IsGungirlCorpse(p.name)) { root = p; old = false; }
                    else if ((IsFlexa(p.name) || IsFlexaCorpse(p.name)) && IsOldMarked(p)) { root = p; old = true; }
                }
                if (root == null || _dressed.Contains(root.gameObject.GetInstanceID())) continue;
                if (old) { Rename(root.gameObject); Plugin.Verbose("Gungirl: " + root.name + " converted from the old scale mark"); }
                Apply(root.gameObject, "restored");
            }
        }

        // ---------- the body swap ----------
        private static bool Apply(GameObject root, string why)
        {
            int swapped = 0;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null || _ours.Contains(smr.sharedMesh)) continue;
                if (smr.bones == null || smr.bones.Length < 10) continue;     // only the body, not props
                var mesh = MeshFor(smr);
                if (mesh == null) return false;
                var mat = MaterialFor(smr.sharedMaterial);
                smr.sharedMesh = mesh;
                if (mat != null) smr.sharedMaterial = mat;
                swapped++;
            }
            int hidden = HideParts(root);
            if (swapped > 0) { Voice.Apply(root); Label(root); _dressed.Add(root.GetInstanceID()); }
            if (swapped > 0) Plugin.Verbose("Gungirl: " + root.name + " (" + why + ") body swapped, " + hidden + " part(s) hidden");
            return swapped > 0;
        }

        // The corpse's mouse-over label (ItemName FSM, state "over": UiTextSetText "Flexa") -> "Gungirl". The FSM's actions exist once it
        // is initialised; Voice's pending list calls this again after the body swap when the FSMs weren't ready yet.
        internal static int Label(GameObject root)
        {
            int n = 0;
            foreach (var f in root.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.FsmName != "ItemName" || f.Fsm == null || !f.Fsm.Initialized) continue;
                foreach (var st in f.Fsm.States)
                {
                    if (st.Actions == null) continue;
                    foreach (var a in st.Actions)
                    {
                        if (a == null || a.GetType().Name != "UiTextSetText") continue;     // by reflection: the action's field types pull in UnityEngine.UI
                        var fld = a.GetType().GetField("text");
                        var cur = fld != null ? fld.GetValue(a) as FsmString : null;
                        if (cur != null && cur.Value == "Flexa") { fld.SetValue(a, new FsmString { Value = Name }); n++; }
                    }
                }
            }
            return n;
        }

        private static int HideParts(GameObject root)
        {
            string list = Plugin.GungirlHideParts.Value ?? "";
            if (list.Trim().Length == 0) return 0;
            var names = new List<string>();
            foreach (var s in list.Split(',')) { var n = s.Trim(); if (n.Length > 0) names.Add(n); }
            int count = 0;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                foreach (var n in names)
                    if (r.gameObject.name.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                    {
                        if (r.enabled) { r.enabled = false; count++; }
                        break;
                    }
            return count;
        }

        private static SkinModel Model()
        {
            if (_modelTried) return _model;
            _modelTried = true;
            string path = ModPath(Plugin.GungirlModel.Value);
            try
            {
                _model = Gltf.Load(path);
                Plugin.Log.LogInfo("Gungirl model: " + Path.GetFileName(path) + " - " + _model.Info);
            }
            catch (Exception e) { Plugin.Log.LogError("Gungirl model " + path + " could not be loaded: " + e.Message); _model = null; }
            return _model;
        }

        private static Mesh MeshFor(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            var names = new string[bones.Length];
            for (int i = 0; i < bones.Length; i++) names[i] = bones[i] != null ? bones[i].name : null;
            string key = string.Join("|", names);
            Mesh cached;
            if (_meshes.TryGetValue(key, out cached)) return cached;
            _meshes[key] = null;   // one attempt per layout

            var model = Model();
            if (model == null) return null;

            // bind poses: the game's own (if the mesh hands them out), else the table read from the asset
            Matrix4x4[] bp = null;
            string bpSource = "game mesh";
            try
            {
                var orig = smr.sharedMesh.bindposes;
                if (orig != null && orig.Length == bones.Length) bp = orig;
            }
            catch (Exception) { }
            if (bp == null)
            {
                bpSource = "built-in table";
                bp = new Matrix4x4[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    float[] v;
                    if (names[i] == null || !Bindposes.Human.TryGetValue(names[i], out v))
                    {
                        Plugin.Log.LogError("Gungirl: no bind pose for bone " + names[i] + " of " + smr.name);
                        return null;
                    }
                    var m = Matrix4x4.identity;
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 4; c++) m[r, c] = v[r * 4 + c];
                    bp[i] = m;
                }
            }

            int rootBone = 0;
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && bones[i] == smr.rootBone) rootBone = i;

            int[] idx4; float[] w4; List<string> unmapped;
            Gltf.Weights(model, names, rootBone, out idx4, out w4, out unmapped);

            int n = model.VertexCount;
            var verts = new Vector3[n]; var nrms = new Vector3[n]; var uvs = new Vector2[n]; var bws = new BoneWeight[n];
            for (int v = 0; v < n; v++)
            {
                verts[v] = new Vector3(model.Pos[v * 3], model.Pos[v * 3 + 1], model.Pos[v * 3 + 2]);
                nrms[v] = new Vector3(model.Nrm[v * 3], model.Nrm[v * 3 + 1], model.Nrm[v * 3 + 2]);
                uvs[v] = new Vector2(model.Uv[v * 2], model.Uv[v * 2 + 1]);
                bws[v] = new BoneWeight
                {
                    boneIndex0 = idx4[v * 4], weight0 = w4[v * 4],
                    boneIndex1 = idx4[v * 4 + 1], weight1 = w4[v * 4 + 1],
                    boneIndex2 = idx4[v * 4 + 2], weight2 = w4[v * 4 + 2],
                    boneIndex3 = idx4[v * 4 + 3], weight3 = w4[v * 4 + 3],
                };
            }
            var mesh = new Mesh { name = "Gungirl" };
            if (n > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = model.Tris;
            if (model.HasNormals) mesh.normals = nrms; else mesh.RecalculateNormals();
            mesh.boneWeights = bws;
            mesh.bindposes = bp;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;

            // how far the model's skeleton is from the game's (should be ~0: same armature, unchanged bones)
            float worst = 0f; string worstName = "";
            var boneOf = new Dictionary<string, int>();
            for (int i = 0; i < names.Length; i++) if (names[i] != null && !boneOf.ContainsKey(names[i])) boneOf[names[i]] = i;
            for (int j = 0; j < model.Joints.Length; j++)
            {
                int b;
                if (!boneOf.TryGetValue(model.Joints[j], out b)) continue;
                Vector3 game = bp[b].inverse.GetColumn(3);
                var mine = new Vector3(model.JointPos[j * 3], model.JointPos[j * 3 + 1], model.JointPos[j * 3 + 2]);
                float d = (game - mine).magnitude;
                if (d > worst) { worst = d; worstName = model.Joints[j]; }
            }
            string msg = "Gungirl mesh for " + smr.name + ": " + n + " vertices, bind poses from " + bpSource
                         + ", skeleton offset max " + (worst * 100f).ToString("0.0") + " cm (" + worstName + ")"
                         + (unmapped.Count > 0 ? ", joints without a game bone (use their parent): " + string.Join(", ", unmapped.ToArray()) : "");
            if (worst > 0.05f) Plugin.Log.LogWarning(msg + " - the armature was moved in Blender; she will be deformed");
            else Plugin.Log.LogInfo(msg);

            _meshes[key] = mesh;
            _ours.Add(mesh);
            return mesh;
        }

        private static Material MaterialFor(Material orig)
        {
            if (orig == null) return null;
            Material m;
            string key = orig.name;
            int cut = key.IndexOf(" (Instance)", StringComparison.Ordinal);
            if (cut > 0) key = key.Substring(0, cut);
            if (_mats.TryGetValue(key, out m) && m != null) return m;
            m = new Material(orig) { name = key + " (Gungirl)" };
            var tex = Texture();
            if (tex != null) m.mainTexture = tex;
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _mats[key] = m;
            return m;
        }

        private static Texture2D Texture()
        {
            if (_texTried) return _tex;
            _texTried = true;
            string path = ModPath(Plugin.GungirlTexture.Value);
            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Gungirl" };
                if (!ImageConversion.LoadImage(tex, bytes, true)) throw new InvalidDataException("not a PNG/JPG");
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _tex = tex;
                Plugin.Log.LogInfo("Gungirl texture: " + Path.GetFileName(path) + " (" + tex.width + "x" + tex.height + ")");
            }
            catch (Exception e) { Plugin.Log.LogError("Gungirl texture " + path + " could not be loaded: " + e.Message); }
            return _tex;
        }

        internal static string ModPath(string rel)
        {
            rel = (rel ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(rel) ? rel : Path.Combine(Plugin.Dir, rel);
        }

        // ---------- debug spawn ----------
        private static void SpawnInFront()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var prefab = FlexaPrefab();
            if (prefab == null) { Plugin.Log.LogWarning("Gungirl: Flexa prefab not found"); return; }
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 at = cam.transform.position + fwd * 6f;
            RaycastHit hit;
            if (Physics.Raycast(at + Vector3.up * 20f, Vector3.down, out hit, 60f, ~0, QueryTriggerInteraction.Ignore))
                at = hit.point;
            // Flexa's pivot is ~1 m above her feet (capsule centre -0.23, height 1.5): lift the pivot so the capsule's
            // bottom sits just above the ground - spawned with the pivot on the ground, she starts half inside the terrain
            // and falls through it.
            at += Vector3.up * (FootDepth(prefab) + 0.05f);
            var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(-fwd) * prefab.transform.rotation);
            Register(go, Name);
            bool ok = Apply(go, "debug key");
            Plugin.Log.LogInfo("Gungirl: spawned " + go.name + " at " + at.ToString("F1") + (ok ? "" : " - but the model could not be applied, see the errors above"));
        }

        // distance from the root pivot down to the bottom of its solid capsule collider (world units)
        private static float FootDepth(GameObject prefab)
        {
            float depth = 0f;
            float sy = Mathf.Abs(prefab.transform.localScale.y);
            foreach (var cap in prefab.GetComponents<CapsuleCollider>())
            {
                if (cap.isTrigger || cap.direction != 1) continue;
                depth = Mathf.Max(depth, (cap.height * 0.5f - cap.center.y) * sy);
            }
            return depth > 0f ? depth : 1f;
        }

        // NPC detection registered an NPC: a Gungirl from a save (or a pre-0.6.0 marked Flexa) is dressed now
        internal static void Registered(GameObject owner)
        {
            try
            {
                if (owner == null || _dressed.Contains(owner.GetInstanceID())) return;
                string n = owner.name;
                if (IsGungirlName(n)) Apply(owner, "restored");
                else if (IsFlexa(n) && IsOldMarked(owner.transform)) { Rename(owner); Apply(owner, "restored"); }
            }
            catch (Exception e) { Plugin.Log.LogError("Gungirl: " + e); }
        }

        internal static GameObject FlexaPrefab()
        {
            if (_prefab != null) return _prefab;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.name == "Flexa" && !go.scene.IsValid() && go.transform.parent == null && go.GetComponent<Rigidbody>() != null)
                { _prefab = go; break; }
            return _prefab;
        }

        // the game's spawn recipe (same as Apocatremors/Apocaspawner): itemNameID number in the name + NewGO_ArrayList items list -> saved
        private static void Register(GameObject go, string prefabName)
        {
            int id = -1;
            var counterGo = GameObject.Find("itemNameID");
            if (counterGo != null)
                foreach (var f in counterGo.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "itemNameID")
                    {
                        var v = f.FsmVariables.GetFsmInt("intName");
                        if (v != null) { v.Value += 1; id = v.Value; }
                        break;
                    }
            go.name = prefabName + "(Clone)" + (id >= 0 ? id.ToString() : "");
            var reg = GameObject.Find("NewGO_ArrayList");
            if (reg == null) return;
            foreach (var p in reg.GetComponents<PlayMakerArrayListProxy>())
                if ((p.referenceName ?? "").ToLowerInvariant().Contains("item")) { p.arrayList.Add(go); return; }
        }
    }
}

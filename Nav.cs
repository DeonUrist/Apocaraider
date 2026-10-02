using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Apocaraiders
{
    // Structure navigation: NPCs know the camps, buildings and caves they are in.
    //
    // The world's structures are instances of 30 prefabs (Camp_1..16, Building_1..7, Cave_1..7; asset read 2026-10-02). When the player
    // comes within [Nav] BakeRange of one, its footprint (the union of its solid colliders + Margin) is baked into a walkability grid
    // (CellSize, world-aligned): per cell one downward ray for the floor nearest the structure's base height (caves have a roof above the
    // floor), then a body-sized capsule from ankle (0.2 m) to head (1.7 m) height must be free of anything solid except cars, loose items
    // and creatures - so spikes at a cave mouth, a brazier, crates and walls are obstacles, the clean opening is not. Neighbouring cells
    // connect when their floors differ by at most MaxStep. Baking is spread over frames (BakeBudgetMs per frame) and kept per instance.
    //
    // Routing: an NPC standing inside a baked footprint whose goal (target or ghost) is not in straight sight on the grid follows a
    // distance field built for that goal (Dijkstra over the grid: from the goal cell when the goal is inside, otherwise from every edge
    // cell weighted by its distance to the goal, so the NPC leaves through the exit that is shortest overall). The next waypoint is the
    // farthest cell along the descent that is in grid sight, handed to the brain's feelers as a waypoint. Fields are cached per goal
    // (shared by every NPC heading there) for FieldSeconds. Outside any footprint nothing runs.
    internal static class Nav
    {
        internal sealed class Structure
        {
            public Transform Root; public string Name;
            public Bounds Box;                      // world bounds of the footprint incl. margin
            public float Cell, RefY; public int W, H;
            public float[] FloorY;                  // NaN = blocked
            public bool Baked; public int Next;     // bake progress (cell index)
            public int Walkable, NoFloor, Tight, Solid; public float BakeMs; public int BakeFrames;
            public byte[] Why;                      // per cell: 0 walkable, 1 no floor, 2 too tight, 3 inside rock
            public bool[] Open;                     // walkable cell with open sky above (not under a cave roof / building)
            public int EdgeCells, EdgeWalkable;     // the outer ring of the footprint
            public Dictionary<int, int> FloorHits;  // collider id -> walkable cells it is the floor of (during the bake)
            public float NextDump;
            public byte[] Edges;                    // per cell, 1 = open edge toward +x (bit 0), +z (1), +x+z (2), -x+z (3): no wall or spike between the two cells
            public int[] Comp; public int[] CompSize;   // connected areas (flood fill over open edges) and their sizes
            public int Phase;                       // bake: 0 floors, 1 edges, 2 done
            public readonly Dictionary<long, Field> Fields = new Dictionary<long, Field>();
        }

        internal sealed class Field { public float[] Dist; public float Made; public bool GoalInside; }

        private static readonly List<Structure> _structures = new List<Structure>();
        private static readonly HashSet<int> _known = new HashSet<int>();
        private static Structure _baking;
        private static float _nextScan, _nextPick;
        private static readonly Stopwatch _sw = new Stopwatch();
        // solid for baking: everything the feelers see, minus cars (8) and loose items (9) - those move
        private static readonly int BakeMask = ~((1 << 1) | (1 << 2) | (1 << 4) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 12) | (1 << 13) | (1 << 15) | (1 << 17) | (1 << 19) | (1 << 22));
        private const float Radius = 0.28f, Ankle = 0.2f, HeadTop = 1.5f;   // a human NPC: capsule r 0.28, 1.5 m tall
        private const int MinArea = 30;          // connected areas smaller than this (7.5 m2) are noise next to props: never start or end a route there
        private static readonly HashSet<int> _floorCols = new HashSet<int>();   // colliders that are the floor of >= FloorColCells map cells (cave floors, camp decks)
        private const int FloorColCells = 40;

        internal static bool On { get { return Plugin.NavEnabled != null && Plugin.NavEnabled.Value; } }

        public static void OnSceneLoaded() { _structures.Clear(); _known.Clear(); _baking = null; _nextScan = 0f; _debug.Clear(); _floorCols.Clear(); _exits.Clear(); }

        // ---------- discovery + baking (per frame) ----------
        public static void Tick()
        {
            if (!On) return;
            float now = Time.unscaledTime;
            if (now >= _nextScan) { _nextScan = now + 10f; Discover(); }
            var player = Player();
            if (player == null) return;
            if (_baking == null && now >= _nextPick)
            {
                _nextPick = now + 1f;
                float best = float.MaxValue;
                foreach (var s in _structures)
                {
                    if (s.Baked || s.Root == null) continue;
                    float d = Mathf.Sqrt(s.Box.SqrDistance(player.position));
                    if (d < Plugin.NavBakeRange.Value && d < best) { best = d; _baking = s; }
                }
                if (_baking != null) BeginBake(_baking);
            }
            if (_baking != null)
            {
                try { BakeStep(_baking); }
                catch (Exception e) { Plugin.Log.LogError("Nav: bake of " + _baking.Name + " failed: " + e); _baking.Baked = true; _baking.FloorY = null; _baking = null; }
            }
        }

        private static Transform _player; private static float _nextPlayer;
        private static Transform Player()
        {
            if (_player == null && Time.unscaledTime >= _nextPlayer) { _nextPlayer = Time.unscaledTime + 2f; var g = GameObject.Find("Player"); _player = g != null ? g.transform : null; }
            return _player;
        }

        // structures are found by name (Camp_N / Building_N / Cave_N, any "(Clone)" suffix) among the scene roots and their children
        private static readonly List<GameObject> _roots = new List<GameObject>();
        private static void Discover()
        {
            int added = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                _roots.Clear(); sc.GetRootGameObjects(_roots);
                foreach (var r in _roots) added += Scan(r.transform, 0);
            }
            if (added > 0 && Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: " + added + " new structure(s), " + _structures.Count + " known");
        }

        private static int Scan(Transform t, int depth)
        {
            if (IsStructure(t.name))
            {
                if (!t.gameObject.activeInHierarchy || !_known.Add(t.GetInstanceID())) return 0;
                var s = Make(t);
                if (s == null) return 0;
                _structures.Add(s);
                if (Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: found " + Path(t) + " at " + t.position + ", footprint " + s.Box.size.x.ToString("0") + " x " + s.Box.size.z.ToString("0") + " m");
                return 1;
            }
            if (depth >= 4 || t.childCount > 3000) return 0;
            if (t.GetComponent<Rigidbody>() != null) return 0;       // creatures, cars, items: never contain structures
            int n = 0;
            for (int i = 0; i < t.childCount; i++) n += Scan(t.GetChild(i), depth + 1);
            return n;
        }

        private static bool IsStructure(string name)
        {
            string p = name.StartsWith("Camp_") ? "Camp_" : name.StartsWith("Building_") ? "Building_" : name.StartsWith("Cave_") ? "Cave_" : null;
            if (p == null || name.Length <= p.Length || !char.IsDigit(name[p.Length])) return false;
            for (int i = p.Length; i < name.Length; i++) { char c = name[i]; if (!char.IsDigit(c)) return c == '(' || c == ' '; }
            return true;
        }

        private static string Path(Transform t) { return t.parent != null ? t.parent.name + "/" + t.name : t.name; }

        private static Structure Make(Transform root)
        {
            bool any = false; Bounds b = new Bounds();
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                int l = c.gameObject.layer;
                if (((1 << l) & BakeMask) == 0) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any) return null;
            float margin = Mathf.Max(1f, Plugin.NavMargin.Value);
            b.Expand(new Vector3(margin * 2f, 0f, margin * 2f));
            float cell = Mathf.Max(0.25f, Plugin.NavCellSize.Value);
            float maxSide = Mathf.Max(b.size.x, b.size.z);
            if (maxSide / cell > 600f) cell = maxSide / 600f;           // very large footprints get coarser cells (<= 600 x 600)
            var s = new Structure { Root = root, Name = root.name, Box = b, Cell = cell };
            s.W = Mathf.Max(2, Mathf.CeilToInt(b.size.x / cell)); s.H = Mathf.Max(2, Mathf.CeilToInt(b.size.z / cell));
            return s;
        }

        private static void BeginBake(Structure s)
        {
            s.FloorY = new float[s.W * s.H]; s.Why = new byte[s.W * s.H]; s.Open = new bool[s.W * s.H]; s.FloorHits = new Dictionary<int, int>();
            s.Edges = new byte[s.W * s.H]; s.Comp = null; s.CompSize = null; s.Phase = 0;
            s.Next = 0; s.Walkable = 0; s.NoFloor = 0; s.Tight = 0; s.Solid = 0; s.BakeMs = 0f; s.BakeFrames = 0;
            // the base height: the floor under the structure's pivot (a ray from well above, first walkable surface below the pivot + 2 m)
            Vector3 p = s.Root.position;
            s.RefY = p.y;
            RaycastHit h;
            if (Physics.Raycast(new Vector3(p.x, p.y + 2f, p.z), Vector3.down, out h, 12f, BakeMask, QueryTriggerInteraction.Ignore)) s.RefY = h.point.y;
        }

        private static void BakeStep(Structure s)
        {
            if (s.Root == null) { _baking = null; return; }
            _sw.Reset(); _sw.Start();
            float budget = Mathf.Max(0.2f, Plugin.NavBakeBudgetMs.Value);
            int n = s.W * s.H;
            if (s.Phase == 1) { BakeEdges(s, budget); return; }
            while (s.Next < n && _sw.Elapsed.TotalMilliseconds < budget)
            {
                int i = s.Next++;
                Collider fc; byte why; bool open;
                s.FloorY[i] = Floor(s, i % s.W, i / s.W, out fc, out why, out open);
                s.Why[i] = why; s.Open[i] = open;
                if (!float.IsNaN(s.FloorY[i]))
                {
                    s.Walkable++;
                    if (fc != null) { int id = fc.GetInstanceID(), c; s.FloorHits.TryGetValue(id, out c); s.FloorHits[id] = c + 1; }
                }
            }
            _sw.Stop();
            s.BakeMs += (float)_sw.Elapsed.TotalMilliseconds; s.BakeFrames++;
            if (s.Next >= n) { s.Phase = 1; s.Next = 0; }
        }

        // second pass: which neighbouring walkable cells can a body walk between - a thin wall, a cave's rock shell or a spike between two
        // free cells blocks the edge (two lines, knee and chest high, with back faces on so a one-sided mesh blocks from both sides)
        private static readonly int[] EdgeDx = { 1, 0, 1, -1 }, EdgeDz = { 0, 1, 1, 1 };
        private static void BakeEdges(Structure s, float budget)
        {
            int n = s.W * s.H;
            bool old = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                while (s.Next < n && _sw.Elapsed.TotalMilliseconds < budget)
                {
                    int i = s.Next++;
                    float ya = s.FloorY[i];
                    if (float.IsNaN(ya)) continue;
                    int x = i % s.W, z = i / s.W; byte bits = 0;
                    Vector3 a = CellCenter(s, x, z, ya);
                    for (int k = 0; k < 4; k++)
                    {
                        int cx = x + EdgeDx[k], cz = z + EdgeDz[k];
                        if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                        float yb = s.FloorY[cz * s.W + cx];
                        if (float.IsNaN(yb) || Mathf.Abs(ya - yb) > Mathf.Max(0.1f, Plugin.NavMaxStep.Value)) continue;
                        Vector3 b = CellCenter(s, cx, cz, yb);
                        if (Physics.Linecast(a + Vector3.up * 0.5f, b + Vector3.up * 0.5f, BakeMask, QueryTriggerInteraction.Ignore)) continue;
                        if (Physics.Linecast(a + Vector3.up * 1.2f, b + Vector3.up * 1.2f, BakeMask, QueryTriggerInteraction.Ignore)) continue;
                        bits |= (byte)(1 << k);
                    }
                    s.Edges[i] = bits;
                }
            }
            finally { Physics.queriesHitBackfaces = old; }
            _sw.Stop();
            s.BakeMs += (float)_sw.Elapsed.TotalMilliseconds; s.BakeFrames++;
            if (s.Next >= n)
            {
                s.Phase = 2;
                Components(s);
                s.Baked = true; _baking = null;
                int floorCols = 0;
                foreach (var kv in s.FloorHits) if (kv.Value >= FloorColCells && _floorCols.Add(kv.Key)) floorCols++;
                s.FloorHits = null;
                s.EdgeCells = 0; s.EdgeWalkable = 0;
                for (int i = 0; i < n; i++) if (IsEdge(s, i)) { s.EdgeCells++; if (!float.IsNaN(s.FloorY[i])) s.EdgeWalkable++; }
                int open = 0; for (int i = 0; i < n; i++) if (s.Open[i]) open++;
                int areas = 0, biggest = 0, islands = 0, walls = 0;
                foreach (int sz in s.CompSize) { if (sz >= MinArea) areas++; else islands += sz; if (sz > biggest) biggest = sz; }
                for (int i = 0; i < n; i++) if (WallNext(s, i)) walls++;
                if (Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: baked " + s.Name + ": " + s.W + " x " + s.H + " cells of " + s.Cell.ToString("0.00") + " m, " + s.Walkable + " walkable (" + (100f * s.Walkable / Mathf.Max(1, s.W * s.H)).ToString("0") + " %, " + open + " under open sky), "
                    + s.NoFloor + " no floor, " + s.Tight + " too tight, " + s.Solid + " inside rock; outer ring " + s.EdgeWalkable + "/" + s.EdgeCells + " walkable; " + floorCols + " new floor collider(s); " + areas + " area(s), largest " + biggest + " cells, " + islands + " cells in small islands, " + walls + " cells with a wall to a neighbour; "
                    + s.BakeMs.ToString("0") + " ms over " + s.BakeFrames + " frames");
                if (Plugin.NavDump.Value) Dump(s, "baked", -1, -1, -1, null);
            }
        }

        // the floor of a cell: the lowest walkable surface (upward facing, room for a body above it, not inside rock) within 6 m of the base height.
        // Ray by ray from the top down (a multi-hit query reports one hit per collider, and a cave's roof and floor can be one mesh; ray
        // casts skip back faces, so the inside of a cave roof is passed through and its floor is found).
        // connected areas over open edges (flood fill); the size of each
        private static void Components(Structure s)
        {
            int n = s.W * s.H;
            s.Comp = new int[n];
            for (int i = 0; i < n; i++) s.Comp[i] = -1;
            var sizes = new List<int>(); var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (s.Comp[i] >= 0 || float.IsNaN(s.FloorY[i])) continue;
                int id = sizes.Count, size = 0;
                s.Comp[i] = id; stack.Push(i);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); size++;
                    int x = c % s.W, z = c / s.W;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int cx = x + dx, cz = z + dz;
                            if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                            int j = cz * s.W + cx;
                            if (s.Comp[j] >= 0 || !Step(s, c, j)) continue;
                            s.Comp[j] = id; stack.Push(j);
                        }
                }
                sizes.Add(size);
            }
            s.CompSize = sizes.ToArray();
        }

        private static float Floor(Structure s, int x, int z, out Collider col, out byte why, out bool open)
        {
            Vector3 c = CellCenter(s, x, z, s.RefY);
            float y0 = s.Box.max.y + 1f, bottom = s.RefY - 8f;
            float best = float.NaN; col = null; why = 1; open = false;
            RaycastHit h;
            for (int k = 0; k < 8 && y0 > bottom; k++)
            {
                if (!Physics.Raycast(new Vector3(c.x, y0, c.z), Vector3.down, out h, y0 - bottom, BakeMask, QueryTriggerInteraction.Ignore)) break;
                y0 = h.point.y - 0.05f;
                if (h.normal.y < 0.6f) continue;                       // a wall or a steep rock face
                float y = h.point.y;
                if (y > s.RefY + 6f) continue;                          // roofs, rock tops above the structure
                if (y < s.RefY - 6f) break;
                Vector3 f = new Vector3(c.x, y, c.z);
                if (InsideSolid(f)) { s.Solid++; why = 3; continue; }
                if (!BodyFits(f)) { s.Tight++; why = 2; continue; }
                best = y; col = h.collider; why = 0;                                              // keep going: the LOWEST free surface is the floor (a wreck's deck,
            }                                                           // a crate top or a cave roof above it is not where NPCs walk)
            if (float.IsNaN(best)) { if (why == 1) s.NoFloor++; }
            else open = !Physics.Raycast(new Vector3(c.x, best + 1.6f, c.z), Vector3.up, 40f, BakeMask, QueryTriggerInteraction.Ignore);
            return best;
        }

        // Something the brain's sweeps hit is floor, not an obstacle, when it faces up (a slope you can walk) and is the terrain or a collider
        // that the maps found to be the floor of a large area (a cave's rock mesh, which is one Default-layer collider for floor, walls and
        // roof; a camp's deck). A brazier, a crate or a spike is never one of those, so its top still counts as an obstacle.
        internal static bool IsFloor(Collider c, Vector3 normal)
        {
            if (c == null || normal.y <= 0.6f) return false;
            return c.gameObject.layer == 14 || _floorCols.Contains(c.GetInstanceID());
        }
        internal static bool IsFloorCollider(Collider c) { return c != null && (c.gameObject.layer == 14 || _floorCols.Contains(c.GetInstanceID())); }

        // An overlap test against a non-convex mesh collider (a cave's rock) only sees its triangles, so a capsule wholly inside the rock
        // passes as free and the terrain under the rock looked like floor. One ray up with back faces on: from inside solid rock it meets the
        // inside of the rock's surface; under a real roof there is open air up to head height.
        private static bool InsideSolid(Vector3 floor)
        {
            bool old = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try { return Physics.Raycast(floor + Vector3.up * 0.05f, Vector3.up, HeadTop, BakeMask, QueryTriggerInteraction.Ignore); }
            finally { Physics.queriesHitBackfaces = old; }
        }

        private static bool BodyFits(Vector3 f)
        {
            return !Physics.CheckCapsule(f + Vector3.up * (Ankle + Radius), f + Vector3.up * (HeadTop - Radius), Radius, BakeMask, QueryTriggerInteraction.Ignore);
        }

        // for the log: why the map has nothing walkable where an NPC stands
        internal static string Probe(Vector3 pos)
        {
            foreach (var s in _structures)
            {
                if (!s.Baked || s.FloorY == null || s.Root == null || !Inside(s, pos)) continue;
                int x, z; CellOf(s, pos, out x, out z);
                var sb = new System.Text.StringBuilder();
                sb.Append(" [cell ").Append(x).Append(',').Append(z).Append(" floor ").Append(float.IsNaN(s.FloorY[z * s.W + x]) ? "none" : s.FloorY[z * s.W + x].ToString("0.0")).Append(", NPC y ").Append(pos.y.ToString("0.0"));
                // re-run the tests at the NPC's own feet
                Vector3 f = new Vector3(pos.x, pos.y - 0.98f, pos.z);
                RaycastHit h;
                if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out h, 3f, BakeMask, QueryTriggerInteraction.Ignore)) { f.y = h.point.y; sb.Append(", ground ").Append(h.collider.name).Append(" n.y ").Append(h.normal.y.ToString("0.00")); }
                else sb.Append(", no ground under it");
                sb.Append(InsideSolid(f) ? ", inside solid" : ", not inside solid");
                sb.Append(BodyFits(f) ? ", body fits" : ", body does not fit (0.2-1.5 m, r 0.28)");
                int w = 0; for (int dz = -4; dz <= 4; dz++) for (int dx = -4; dx <= 4; dx++) { int cx = x + dx, cz = z + dz; if (cx >= 0 && cz >= 0 && cx < s.W && cz < s.H && !float.IsNaN(s.FloorY[cz * s.W + cx])) w++; }
                sb.Append(", walkable within 2 m: ").Append(w).Append("/81]");
                return sb.ToString();
            }
            return "";
        }

        private static Vector3 CellCenter(Structure s, int x, int z, float y)
        {
            return new Vector3(s.Box.min.x + (x + 0.5f) * s.Cell, y, s.Box.min.z + (z + 0.5f) * s.Cell);
        }

        private static bool CellOf(Structure s, Vector3 p, out int x, out int z)
        {
            x = Mathf.FloorToInt((p.x - s.Box.min.x) / s.Cell); z = Mathf.FloorToInt((p.z - s.Box.min.z) / s.Cell);
            return x >= 0 && z >= 0 && x < s.W && z < s.H;
        }

        private static bool Inside(Structure s, Vector3 p) { int x, z; return CellOf(s, p, out x, out z); }

        // the nearest walkable cell within r cells (an NPC hugging a wall stands in a blocked cell)
        // the cell an NPC really stands in / next to: nearest cells first, not in a tiny island, with no wall between the NPC and the cell
        private static readonly List<int> _cand = new List<int>();
        private static int NearestReachable(Structure s, Vector3 pos, int x, int z, int r)
        {
            _cand.Clear();
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                    int i = cz * s.W + cx;
                    if (float.IsNaN(s.FloorY[i]) || !BigArea(s, i)) continue;
                    _cand.Add(i);
                }
            _cand.Sort((a, b) => Dist2(s, a, x, z).CompareTo(Dist2(s, b, x, z)));
            int tried = 0;
            foreach (int i in _cand)
            {
                Vector3 c = CellCenter(s, i % s.W, i / s.W, s.FloorY[i]);
                if (!Physics.Linecast(new Vector3(pos.x, c.y + 0.5f, pos.z), c + Vector3.up * 0.5f, BakeMask, QueryTriggerInteraction.Ignore)) return i;
                if (++tried >= 12) break;
            }
            return _cand.Count > 0 ? _cand[0] : -1;
        }
        private static int Dist2(Structure s, int i, int x, int z) { int dx = i % s.W - x, dz = i / s.W - z; return dx * dx + dz * dz; }

        private static int NearestWalkable(Structure s, int x, int z, int r)
        {
            int best = -1; int bestD = int.MaxValue;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                    int i = cz * s.W + cx;
                    if (float.IsNaN(s.FloorY[i]) || !BigArea(s, i)) continue;
                    int dd = dx * dx + dz * dz;
                    if (dd < bestD) { bestD = dd; best = i; }
                }
            return best;
        }

        private static bool Step(Structure s, int a, int b)
        {
            float ya = s.FloorY[a], yb = s.FloorY[b];
            if (float.IsNaN(ya) || float.IsNaN(yb) || Mathf.Abs(ya - yb) > Mathf.Max(0.1f, Plugin.NavMaxStep.Value)) return false;
            if (s.Edges == null || s.Phase < 2) return true;
            int ax = a % s.W, az = a / s.W, bx = b % s.W, bz = b / s.W;
            int dx = bx - ax, dz = bz - az;
            if (dz < 0 || (dz == 0 && dx < 0)) { int t = a; a = b; b = t; dx = -dx; dz = -dz; }
            int k = dz == 0 ? 0 : dx == 0 ? 1 : dx > 0 ? 2 : 3;
            return (s.Edges[a] & (1 << k)) != 0;
        }

        // a walkable cell with a walkable neighbour at a walkable height that it still can't reach (a wall / spike between them)
        private static bool WallNext(Structure s, int i)
        {
            if (float.IsNaN(s.FloorY[i]) || s.Edges == null) return false;
            int x = i % s.W, z = i / s.W;
            for (int k = 0; k < 4; k++)
            {
                int cx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), cz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                int j = cz * s.W + cx; float o = s.FloorY[j];
                if (float.IsNaN(o) || Mathf.Abs(o - s.FloorY[i]) > Mathf.Max(0.1f, Plugin.NavMaxStep.Value)) continue;
                if (!Step(s, i, j)) return true;
            }
            return false;
        }

        private static bool BigArea(Structure s, int i) { return s.Comp == null || (s.Comp[i] >= 0 && s.CompSize[s.Comp[i]] >= MinArea); }

        // ---------- routing ----------
        // A waypoint toward goal for an NPC at pos, or false when no structure is involved / the way is straight. pathLeft = path length to the
        // goal (inside) or to the exit plus the straight rest (outside), for the brain's progress check.
        internal static string LastReason = "";
        internal static bool Next(GameObject owner, Vector3 pos, Vector3 goal, out Vector3 next, out float pathLeft)
        {
            next = goal; pathLeft = 0f; LastReason = "";
            if (!On) return false;
            Structure s = null;
            foreach (var t in _structures) if (t.Baked && t.FloorY != null && t.Root != null && Inside(t, pos)) { s = t; break; }
            if (s == null) { LastReason = ""; return false; }
            int x, z; CellOf(s, pos, out x, out z);
            int from = NearestReachable(s, pos, x, z, 6);
            if (from < 0) { LastReason = "no free map cell near it in " + s.Name; return false; }
            if (Mathf.Abs(pos.y - s.FloorY[from]) > 2.5f) { LastReason = "not on the floor of " + s.Name; return false; }   // on the roof of a cave, on a rock above a camp: not on this map
            int gx, gz;
            bool goalInside = CellOf(s, goal, out gx, out gz);
            int goalCell = goalInside ? NearestWalkable(s, gx, gz, 4) : -1;
            if (goalInside && (goalCell < 0 || Mathf.Abs(goal.y - s.FloorY[goalCell]) > 3f)) goalInside = false;   // in a wall / above the map: outside
            if (goalInside && GridSight(s, from, goalCell)) { LastReason = "straight line to the goal on the " + s.Name + " map"; return false; }  // straight across the floor: the feelers do the rest
            if (!goalInside && IsEdge(s, from)) { LastReason = "at the edge of " + s.Name; return false; }             // already at the edge of the footprint: out we go

            var f = FieldFor(s, goal, goalInside, goalCell);
            float d0 = f.Dist[from];
            if (float.IsInfinity(d0))
            {
                // The map has no way from here to the goal (the goal is outside and the footprint's outer ring can't be reached, or the goal sits
                // on a part of the map this spot doesn't connect to). Then the map's only job is to get the NPC out into the open: head for the
                // reachable open-sky cell that is best overall (path to it + straight line from it to the goal) - the cave mouth, the yard
                // outside a building - and the feelers take it from there.
                int exit = ExitCell(owner, s, from, goal, goalInside, goalCell);
                if (exit < 0 || exit == from) { LastReason = "no way out of this spot on the " + s.Name + " map" + (exit == from ? " (already at the best open spot)" : ""); return false; }
                f = FieldFor(s, CellCenter(s, exit % s.W, exit / s.W, s.FloorY[exit]), true, exit);
                d0 = f.Dist[from];
                if (float.IsInfinity(d0)) { LastReason = "no way to the exit on the " + s.Name + " map"; return false; }
                Vector3 ec = CellCenter(s, exit % s.W, exit / s.W, 0f);
                d0 += new Vector2(goal.x - ec.x, goal.z - ec.z).magnitude;
                if (GridSight(s, from, exit))
                {
                    next = CellCenter(s, exit % s.W, exit / s.W, s.FloorY[exit]);
                    pathLeft = d0; Remember(owner, next, s); return true;
                }
            }
            pathLeft = d0;
            // descend the field up to 16 cells, keep the farthest cell still in grid sight
            int cur = from, pick = from;
            for (int k = 0; k < 16; k++)
            {
                int nb = Downhill(s, f, cur);
                if (nb < 0) break;
                cur = nb;
                if (GridSight(s, from, cur)) pick = cur; else break;
            }
            if (pick == from) { int nb = Downhill(s, f, from); if (nb < 0) { LastReason = "no downhill cell on the " + s.Name + " map"; return false; } pick = nb; }
            next = CellCenter(s, pick % s.W, pick / s.W, s.FloorY[pick]);
            Remember(owner, next, s);
            return true;
        }

        // ---------- the way out when the map can't reach the goal ----------
        private sealed class ExitMemo { public int Exit; public float Until; public int GoalKey; }
        private static readonly Dictionary<int, ExitMemo> _exits = new Dictionary<int, ExitMemo>();
        private static int ExitCell(GameObject owner, Structure s, int from, Vector3 goal, bool goalInside, int goalCell)
        {
            int oid = owner != null ? owner.GetInstanceID() : 0;
            int gkey = Mathf.FloorToInt(goal.x / 4f) * 73856093 ^ Mathf.FloorToInt(goal.z / 4f) * 19349663;
            ExitMemo m;
            float now = Time.time;
            if (_exits.TryGetValue(oid, out m) && now < m.Until && m.GoalKey == gkey && m.Exit >= 0 && m.Exit < s.W * s.H && !float.IsNaN(s.FloorY[m.Exit])) return m.Exit;
            // distances from the NPC over its part of the map
            var mine = Build(s, goal, true, from);
            int n = s.W * s.H, best = -1, bestAny = -1, size = 0, open = 0; bool edge = false;
            float bc = float.MaxValue, bca = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float d = mine.Dist[i];
                if (float.IsInfinity(d)) continue;
                size++;
                if (IsEdge(s, i)) edge = true;
                Vector3 c = CellCenter(s, i % s.W, i / s.W, 0f);
                float cost = d + new Vector2(goal.x - c.x, goal.z - c.z).magnitude;
                if (cost < bca) { bca = cost; bestAny = i; }
                if (s.Open == null || !s.Open[i]) continue;
                open++;
                if (cost < bc) { bc = cost; best = i; }
            }
            if (best < 0) best = bestAny;
            _exits[oid] = new ExitMemo { Exit = best, Until = now + 2f, GoalKey = gkey };
            if (Plugin.NavLog.Value)
                Plugin.Log.LogInfo("Nav: " + (owner != null ? owner.name : "?") + " has no map route to its goal on " + s.Name + " (goal " + (goalInside ? "inside, cell " + (goalCell % s.W) + "," + (goalCell / s.W) : "outside the footprint")
                    + " at " + goal.x.ToString("0") + "," + goal.z.ToString("0") + "; its area " + size + " cells, " + open + " under open sky, " + (edge ? "reaches" : "does NOT reach") + " the outer ring ("
                    + s.EdgeWalkable + "/" + s.EdgeCells + " ring cells walkable)) -> " + (best < 0 ? "nowhere to go" : "heads for cell " + (best % s.W) + "," + (best / s.W) + (s.Open != null && s.Open[best] ? " (open sky)" : " (no open sky in its area)")));
            if (Plugin.NavDump.Value && now >= s.NextDump)
            {
                s.NextDump = now + 20f;
                Dump(s, "noroute_" + (owner != null ? owner.name.Replace("(Clone)", "") : "npc"), from, goalInside ? goalCell : -1, best, mine.Dist);
            }
            return best;
        }

        // ---------- [Debug] NavDump: the map as a picture ----------
        // (also: orange = a wall or spike between this cell and a walkable neighbour; purple = a small island, never used for routes)
        // BepInEx/config/Apocaraiders/NavDump/<structure>_<x>_<z>_<tag>.bmp, 2 px per cell, north up. Walkable: grey by height (open sky
        // greenish, under a roof bluish); a walkable cell next to a walkable one more than MaxStep higher or lower: yellow; no floor: black;
        // too tight for a body: red; inside rock: brown. On a "no route" dump: the NPC's reachable area is tinted, the NPC white, the goal
        // magenta, the chosen exit cyan.
        private static void Dump(Structure s, string tag, int npc, int goalCell, int exit, float[] area)
        {
            try
            {
                int W = s.W, H = s.H, sc = 2, pw = W * sc, ph = H * sc, row = (pw * 3 + 3) & ~3;
                var px = new byte[row * ph];
                float step = Mathf.Max(0.1f, Plugin.NavMaxStep.Value);
                for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = z * W + x; byte r, g, b;
                        if (float.IsNaN(s.FloorY[i]))
                        {
                            switch (s.Why[i]) { case 2: r = 200; g = 30; b = 30; break; case 3: r = 110; g = 70; b = 30; break; default: r = 0; g = 0; b = 0; break; }
                        }
                        else
                        {
                            float t = Mathf.Clamp01((s.FloorY[i] - s.RefY + 3f) / 6f);
                            int v = (int)(90 + 140 * t);
                            if (s.Open[i]) { r = (byte)(v * 0.85f); g = (byte)v; b = (byte)(v * 0.8f); } else { r = (byte)(v * 0.75f); g = (byte)(v * 0.8f); b = (byte)v; }
                            bool cliff = false;
                            for (int k = 0; k < 4 && !cliff; k++)
                            {
                                int cx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), cz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                                if (cx < 0 || cz < 0 || cx >= W || cz >= H) continue;
                                float o = s.FloorY[cz * W + cx];
                                if (!float.IsNaN(o) && Mathf.Abs(o - s.FloorY[i]) > step) cliff = true;
                            }
                            if (cliff) { r = 240; g = 220; b = 40; }
                            if (WallNext(s, i)) { r = 255; g = 130; b = 0; }
                            if (!BigArea(s, i)) { r = 150; g = 60; b = 170; }
                            if (area != null && !float.IsInfinity(area[i])) { r = (byte)(r * 0.6f); g = (byte)(g * 0.6f + 80); b = (byte)(b * 0.6f + 60); }
                        }
                        if (i == exit) { r = 0; g = 255; b = 255; }
                        if (i == goalCell) { r = 255; g = 0; b = 255; }
                        if (i == npc) { r = 255; g = 255; b = 255; }
                        for (int dy = 0; dy < sc; dy++)
                            for (int dx = 0; dx < sc; dx++)
                            {
                                int o = (z * sc + dy) * row + (x * sc + dx) * 3;
                                px[o] = b; px[o + 1] = g; px[o + 2] = r;
                            }
                    }
                // marks a little bigger so they show
                foreach (var m in new[] { npc, goalCell, exit })
                {
                    if (m < 0) continue;
                    int mx = m % W, mz = m / W;
                    byte r = m == npc ? (byte)255 : m == goalCell ? (byte)255 : (byte)0, g = m == npc ? (byte)255 : m == goalCell ? (byte)0 : (byte)255, b = 255;
                    for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
                    {
                        int cx = mx + dx, cz = mz + dz; if (cx < 0 || cz < 0 || cx >= W || cz >= H) continue;
                        for (int yy = 0; yy < sc; yy++) for (int xx = 0; xx < sc; xx++) { int o = (cz * sc + yy) * row + (cx * sc + xx) * 3; px[o] = b; px[o + 1] = g; px[o + 2] = r; }
                    }
                }
                string dir = System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "Apocaraiders"), "NavDump");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, s.Name.Replace("(Clone)", "") + "_" + Mathf.RoundToInt(s.Root.position.x) + "_" + Mathf.RoundToInt(s.Root.position.z) + "_" + tag + ".bmp");
                using (var fs = new System.IO.FileStream(file, System.IO.FileMode.Create))
                using (var w = new System.IO.BinaryWriter(fs))
                {
                    w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + px.Length); w.Write(0); w.Write(54);
                    w.Write(40); w.Write(pw); w.Write(ph); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(px.Length); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
                    w.Write(px);
                }
                Plugin.Log.LogInfo("Nav: map picture " + file + " (cell " + s.Cell.ToString("0.00") + " m, origin " + s.Box.min.x.ToString("0.0") + "," + s.Box.min.z.ToString("0.0") + ", base y " + s.RefY.ToString("0.0") + ")");
            }
            catch (Exception e) { Plugin.Log.LogError("Nav: dump failed: " + e.Message); }
        }

        private static bool IsEdge(Structure s, int i) { int x = i % s.W, z = i / s.W; return x == 0 || z == 0 || x == s.W - 1 || z == s.H - 1; }

        private static int Downhill(Structure s, Field f, int i)
        {
            int x = i % s.W, z = i / s.W; float best = f.Dist[i]; int bi = -1;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                    int j = cz * s.W + cx;
                    if (!Step(s, i, j)) continue;
                    if (dx != 0 && dz != 0 && (!Step(s, i, z * s.W + cx) || !Step(s, i, cz * s.W + x))) continue;
                    if (f.Dist[j] < best) { best = f.Dist[j]; bi = j; }
                }
            return bi;
        }

        // every cell on the grid line between two cells walkable and step-connected (a body-wide corridor, since cells are body-checked)
        private static bool GridSight(Structure s, int a, int b)
        {
            int x0 = a % s.W, z0 = a / s.W, x1 = b % s.W, z1 = b / s.W;
            int dx = Math.Abs(x1 - x0), dz = Math.Abs(z1 - z0), sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1, err = dx - dz;
            int prev = a;
            while (true)
            {
                if (x0 == x1 && z0 == z1) return true;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; x0 += sx; }
                if (e2 < dx) { err += dx; z0 += sz; }
                int i = z0 * s.W + x0;
                if (!Step(s, prev, i)) return false;
                prev = i;
            }
        }

        private static Field FieldFor(Structure s, Vector3 goal, bool inside, int goalCell)
        {
            float now = Time.time;
            long key = inside ? goalCell : (long)1 << 40 | (long)(Mathf.FloorToInt(goal.x / 4f) & 0xFFFFF) << 20 | (long)(Mathf.FloorToInt(goal.z / 4f) & 0xFFFFF);
            Field f;
            if (s.Fields.TryGetValue(key, out f) && now - f.Made < Mathf.Max(0.2f, Plugin.NavFieldSeconds.Value)) return f;
            if (s.Fields.Count > 32) s.Fields.Clear();
            f = Build(s, goal, inside, goalCell);
            s.Fields[key] = f;
            return f;
        }

        // Dijkstra over the grid (8 neighbours, no corner cutting, step limit)
        private static Field Build(Structure s, Vector3 goal, bool inside, int goalCell)
        {
            int n = s.W * s.H;
            var dist = new float[n];
            for (int i = 0; i < n; i++) dist[i] = float.PositiveInfinity;
            var heap = new Heap(Math.Max(64, n / 4));
            if (inside) { dist[goalCell] = 0f; heap.Push(goalCell, 0f); }
            else
            {
                for (int x = 0; x < s.W; x++) { Seed(s, x, 0, goal, dist, heap); Seed(s, x, s.H - 1, goal, dist, heap); }
                for (int z = 1; z < s.H - 1; z++) { Seed(s, 0, z, goal, dist, heap); Seed(s, s.W - 1, z, goal, dist, heap); }
            }
            float c1 = s.Cell, c2 = s.Cell * 1.41421356f;
            int i0; float d;
            while (heap.Pop(out i0, out d))
            {
                if (d > dist[i0]) continue;
                int x = i0 % s.W, z = i0 / s.W;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int cx = x + dx, cz = z + dz;
                        if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                        int j = cz * s.W + cx;
                        if (!Step(s, i0, j)) continue;
                        if (dx != 0 && dz != 0 && (!Step(s, i0, z * s.W + cx) || !Step(s, i0, cz * s.W + x))) continue;
                        float nd = d + (dx != 0 && dz != 0 ? c2 : c1);
                        if (nd < dist[j]) { dist[j] = nd; heap.Push(j, nd); }
                    }
            }
            return new Field { Dist = dist, Made = Time.time, GoalInside = inside };
        }

        private static void Seed(Structure s, int x, int z, Vector3 goal, float[] dist, Heap heap)
        {
            int i = z * s.W + x;
            if (float.IsNaN(s.FloorY[i])) return;
            Vector3 c = CellCenter(s, x, z, 0f); c.y = goal.y = 0f;
            float d = Vector3.Distance(c, new Vector3(goal.x, 0f, goal.z));
            if (d < dist[i]) { dist[i] = d; heap.Push(i, d); }
        }

        private sealed class Heap
        {
            private int[] _i; private float[] _k; private int _n;
            public Heap(int cap) { _i = new int[cap]; _k = new float[cap]; }
            public void Push(int i, float k)
            {
                if (_n == _i.Length) { Array.Resize(ref _i, _n * 2); Array.Resize(ref _k, _n * 2); }
                int c = _n++;
                while (c > 0) { int p = (c - 1) >> 1; if (_k[p] <= k) break; _i[c] = _i[p]; _k[c] = _k[p]; c = p; }
                _i[c] = i; _k[c] = k;
            }
            public bool Pop(out int i, out float k)
            {
                if (_n == 0) { i = -1; k = 0f; return false; }
                i = _i[0]; k = _k[0];
                int li = _i[--_n]; float lk = _k[_n];
                int c = 0;
                while (true)
                {
                    int a = 2 * c + 1; if (a >= _n) break;
                    int b = a + 1; int m = b < _n && _k[b] < _k[a] ? b : a;
                    if (_k[m] >= lk) break;
                    _i[c] = _i[m]; _k[c] = _k[m]; c = m;
                }
                if (_n > 0) { _i[c] = li; _k[c] = lk; }
                return true;
            }
        }

        // ---------- debug ([Debug] ShowNav) ----------
        private struct Mark { public Vector3 Next; public string Where; public float At; }
        private static readonly Dictionary<GameObject, Mark> _debug = new Dictionary<GameObject, Mark>();
        private static void Remember(GameObject owner, Vector3 next, Structure s)
        {
            if (owner == null || !Plugin.ShowNav.Value) return;
            _debug[owner] = new Mark { Next = next, Where = s.Name, At = Time.time };
        }

        internal static void DrawDebug()
        {
            if (!On || !Plugin.ShowNav.Value) return;
            var cyan = new Color(0.3f, 0.9f, 1f);
            foreach (var s in _structures)
            {
                if (s.Root == null) continue;
                var p = Player();
                if (p != null && s.Box.SqrDistance(p.position) > 150f * 150f) continue;
                Hud.Label(new Vector3(s.Box.center.x, s.RefY + 3f, s.Box.center.z), s.Name + (s.Baked ? (s.FloorY != null ? " baked, " + s.Walkable + "/" + (s.W * s.H) + " cells" : " (bake failed)") : _baking == s ? " baking " + (100 * s.Next / Math.Max(1, s.W * s.H)) + " %" : " not baked"), cyan);
            }
            float now = Time.time;
            var dead = new List<GameObject>();
            foreach (var kv in _debug)
            {
                if (kv.Key == null || now - kv.Value.At > 1.5f) { dead.Add(kv.Key); continue; }
                Hud.Mark(kv.Value.Next + Vector3.up * 0.3f, cyan, 8f);
                Hud.Label(kv.Value.Next + Vector3.up * 0.7f, "nav " + kv.Value.Where, cyan);
            }
            foreach (var k in dead) _debug.Remove(k);
        }

        internal static string Status()
        {
            int b = 0; foreach (var s in _structures) if (s.Baked) b++;
            return _structures.Count + " structures, " + b + " baked";
        }
    }
}

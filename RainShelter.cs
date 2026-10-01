using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Weather;
using UnityEngine;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Keeps the rain out of the dome.
    //
    // How the game does it for its own buildings: the rain is a VFX Graph effect, and drops are
    // cut off below a height map of the whole world's roofs. That map is baked in the editor
    // (MaskController._heightMask, a texture) and turned into a float grid at startup
    // (MaskController.HeightMap, HeightMapResolution x HeightMapResolution cells over WorldSize
    // metres) which is handed to the rain effect. The island didn't exist when the map was baked,
    // so to the rain the dome has no roof.
    //
    // So: raise the height map over the dome to the dome's roof height, and hand the updated map
    // to the rain effect. How the grid lies over the world (origin, axis order, whether it holds
    // metres or 0-1 values) isn't documented, so it's measured first: sample cells are compared
    // with the real roof/ground height under them (a raycast down), for every possible layout,
    // and the layout that matches the world is used. Every step is logged.
    internal static class RainShelter
    {
        private struct Layout
        {
            public bool Centered, ZMajor, FlipX, FlipZ, Normalised;
            public override string ToString() => $"{(Centered ? "centred" : "corner")} origin, {(ZMajor ? "row = z" : "row = x")}, flipX {FlipX}, flipZ {FlipZ}, {(Normalised ? "0-1 values" : "metres")}";
        }

        private static bool _stamped, _gaveUp;
        private static float _next, _startedAt = -1f;
        private static Layout _layout;
        private static readonly Dictionary<int, float> _original = new Dictionary<int, float>();
        private static GraphicsBuffer _buffer;
        private static Texture2D _texture;
        private static readonly List<(VisualEffect vfx, string name, bool isBuffer)> _targets = new List<(VisualEffect, string, bool)>();

        // The rain effect's parameters, by the names the game uses (from its string table).
        private static readonly string[] BufferNames = { "HeightBuffer" };
        private static readonly string[] TextureNames = { "HeightMask", "HeightMap" };

        public static void Clear()
        {
            // Put the map back as the game had it (the island may be rebuilt somewhere else).
            // (In its own method: if a game version lacks these weather classes, only that call
            // fails - and it's caught - rather than this whole method.)
            if (_original.Count > 0) try { RestoreMap(); } catch { }
            _original.Clear();
            try { _buffer?.Dispose(); } catch { }
            _buffer = null;
            if (_texture != null) Object.Destroy(_texture);
            _texture = null;
            _targets.Clear();
            Show(true);
            _rainRenderers.Clear();
            _nextRainScan = 0f;
            _stamped = false; _gaveUp = false; _startedAt = -1f; _next = 0f;
            _mask = null;
        }

        private static MaskController _mask;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RestoreMap()
        {
            var mc = Object.FindObjectOfType<MaskController>();
            var map = mc != null ? mc.HeightMap : null;
            if (map != null) foreach (var kv in _original) if (kv.Key < map.Length) map[kv.Key] = kv.Value;
        }

        // ------------------------------------------------------------ hiding the drops inside

        // While the camera is inside the dome, the rain effects' drops aren't drawn (their
        // simulation keeps running, so it's raining normally the moment you step out). The rain is
        // a camera-following effect, so this is what makes it certain no drop shows in there -
        // whatever the height map does.
        private static bool _hideInside = true;
        private static bool _hidden;
        private static float _nextRainScan;
        private static readonly List<Renderer> _rainRenderers = new List<Renderer>();

        // Is this point inside the dome (on either floor, under its glass)?
        public static bool UnderDome(Vector3 p)
        {
            var site = IslandBuilder.Current?.Site;
            if (site == null) return false;
            var flat = new Vector2(p.x - site.Center.x, p.z - site.Center.z).magnitude;
            float R = IslandBuilder.DomeRadius;
            float floor = site.Center.y + IslandBuilder.GhFloor - 0.5f;
            float roof = site.Center.y + IslandBuilder.GhFloor + IslandBuilder.DomeWall + Mathf.Sqrt(Mathf.Max(0f, R * R - flat * flat));
            return flat < R - 0.2f && p.y > floor && p.y < roof;
        }

        public static void Frame()
        {
            var site = IslandBuilder.Current?.Site;
            var cam = Camera.main;
            if (!_hideInside || site == null || cam == null) { Show(true); return; }

            // Scanning every loaded object is expensive (a frame hitch each time): look often only
            // until the rain effects turn up, then just now and then in case the game adds more.
            if (Time.time >= _nextRainScan) { ScanRain(); _nextRainScan = Time.time + (_rainRenderers.Count > 0 ? 120f : 10f); }

            var p = cam.transform.position;
            var flat = new Vector2(p.x - site.Center.x, p.z - site.Center.z).magnitude;
            float R = IslandBuilder.DomeRadius;
            float floor = site.Center.y + IslandBuilder.GhFloor - 0.5f;
            float roof = site.Center.y + IslandBuilder.GhFloor + IslandBuilder.DomeWall + Mathf.Sqrt(Mathf.Max(0f, R * R - flat * flat));
            bool inside = flat < R - 0.2f && p.y > floor && p.y < roof;
            Show(!inside);
        }

        private static void Show(bool show)
        {
            if (_hidden == !show) return;
            _hidden = !show;
            foreach (var r in _rainRenderers) if (r != null) r.enabled = show;
        }

        // The rain effects: visual effects named for rain, or under a RainController.
        private static void ScanRain()
        {
            int before = _rainRenderers.Count;
            // The game's rain effects all hang under its RainController. Asking for just those
            // (one or two objects) is far cheaper than going through every particle system and
            // visual effect in the scene, which was a hitch of 10-25 ms each time it ran.
            foreach (var rc in Object.FindObjectsOfType<RainController>())
            {
                if (rc == null) continue;
                foreach (var r in rc.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || _rainRenderers.Contains(r)) continue;
                    _rainRenderers.Add(r);
                    if (_hidden) r.enabled = false;
                }
            }
            if (_rainRenderers.Count != before)
                Core.Log.Msg($"Rain effects found ({_rainRenderers.Count}): {string.Join(", ", _rainRenderers.ConvertAll(r => r != null ? r.name : "?"))} - hidden while you're inside the dome.");
        }

        public static void Tick()
        {
            if (_gaveUp || Time.time < _next) return;
            _next = Time.time + (_stamped ? 3f : 2f);
            var site = IslandBuilder.Current?.Site;
            if (site == null) return;
            if (_startedAt < 0f) _startedAt = Time.time;

            try
            {
                if (!_stamped) TryStamp(site);
                else Push();   // the game may hand the rain its own map again - keep ours in place
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Rain shelter over the dome failed: " + e.Message);
                _gaveUp = true;
            }
        }

        private static void TryStamp(Site site)
        {
            var mc = Object.FindObjectOfType<MaskController>();
            Il2CppStructArray<float> map = null;
            int res = 0; float size = 0f; Vector2 mm = default;
            if (mc != null)
            {
                map = mc.HeightMap; res = mc.HeightMapResolution; size = mc.WorldSize; mm = mc.MinMaxHeight;
            }
            if (map == null || res <= 0 || map.Length < res * res || size <= 0f)
            {
                // Not converted yet (it's done in a coroutine after load) - wait, but not forever.
                if (Time.time - _startedAt > 180f)
                {
                    Core.Log.Warning($"The game's rain height map never became available (controller {(mc != null ? "found" : "missing")}) - rain may fall inside the dome.");
                    _gaveUp = true;
                }
                return;
            }

            if (!Calibrate(map, res, size, mm, out _layout, out string report))
            {
                Core.Log.Warning("Couldn't match the rain height map to the world, so it wasn't changed - rain drops are hidden while you're inside the dome instead. " + report);
                _hideInside = true;
                _stamped = true;
                return;
            }
            Core.Log.Msg($"Rain height map: {res} x {res} over {size:0} m (heights {mm.x:0.0}..{mm.y:0.0}); layout: {_layout}. {report}");

            // Raise it over the dome to the dome's roof.
            float cell = size / res;
            float R = IslandBuilder.DomeRadius;
            float baseY = site.Center.y + IslandBuilder.GhFloor + IslandBuilder.DomeWall;
            int changed = 0;
            for (int i = 0; i < res; i++)
            for (int j = 0; j < res; j++)
            {
                var w = CellToWorld(i, j, res, size, _layout);
                float r = new Vector2(w.x - site.Center.x, w.y - site.Center.z).magnitude;
                if (r > R + cell) continue;
                float roof = baseY + Mathf.Sqrt(Mathf.Max(0f, R * R - Mathf.Min(r, R) * Mathf.Min(r, R)));
                float v = _layout.Normalised ? Mathf.InverseLerp(mm.x, mm.y, roof) : roof;
                int idx = Index(i, j, res, _layout);
                if (map[idx] >= v) continue;
                if (!_original.ContainsKey(idx)) _original[idx] = map[idx];
                map[idx] = v;
                changed++;
            }
            _stamped = true;
            Core.Log.Msg($"Rain height map: raised {changed} cell(s) ({cell:0.00} m each) over the dome to its roof.");

            FindTargets();
            Push();
        }

        // Grid cell -> world (x, z) for a layout.
        private static Vector2 CellToWorld(int i, int j, int res, float size, Layout l)
        {
            float u = (i + 0.5f) / res, v = (j + 0.5f) / res;
            if (l.FlipX) u = 1f - u;
            if (l.FlipZ) v = 1f - v;
            float o = l.Centered ? -size * 0.5f : 0f;
            return new Vector2(o + u * size, o + v * size);
        }

        private static int Index(int i, int j, int res, Layout l) => l.ZMajor ? j * res + i : i * res + j;

        // Which layout matches the world: sample cells, compare with the real height under them.
        private static bool Calibrate(Il2CppStructArray<float> map, int res, float size, Vector2 mm, out Layout best, out string report)
        {
            best = default;

            // The surfaces the game's own map was made from: the layers its generator scans.
            int mask = ~0;
            try
            {
                var gen = Resources.FindObjectsOfTypeAll<HeightMaskGenerator>();
                if (gen != null && gen.Length > 0 && gen[0] != null) mask = gen[0]._heightmapLayerMask.value;
            }
            catch { }

            // What's in the map at all.
            float vMin = float.MaxValue, vMax = float.MinValue;
            for (int k = 0; k < map.Length; k += 97) { float v = map[k]; if (v < vMin) vMin = v; if (v > vMax) vMax = v; }

            var rng = new System.Random(1234);
            var cells = new List<(int i, int j)>();
            for (int k = 0; k < 700; k++) cells.Add((rng.Next(res), rng.Next(res)));

            var results = new List<(float score, Layout l, int hits)>();
            foreach (bool centered in new[] { true, false })
            foreach (bool zMajor in new[] { true, false })
            foreach (bool flipX in new[] { false, true })
            foreach (bool flipZ in new[] { false, true })
            {
                var l = new Layout { Centered = centered, ZMajor = zMajor, FlipX = flipX, FlipZ = flipZ };
                int hits = 0, okMetres = 0, okNorm = 0, counted = 0;
                foreach (var (i, j) in cells)
                {
                    var w = CellToWorld(i, j, res, size, l);
                    // Nothing on the scanned layers here = the map's floor value.
                    float expect = mm.x;
                    if (Physics.Raycast(new Vector3(w.x, mm.y + 60f, w.y), Vector3.down, out var hit, mm.y - mm.x + 200f, mask, QueryTriggerInteraction.Ignore))
                    { expect = hit.point.y; hits++; }
                    counted++;
                    float v = map[Index(i, j, res, l)];
                    if (Mathf.Abs(v - expect) < 2f) okMetres++;
                    if (Mathf.Abs(Mathf.Lerp(mm.x, mm.y, v) - expect) < 2f) okNorm++;
                }
                results.Add(((float)okMetres / counted, l, hits));
                var ln = l; ln.Normalised = true;
                results.Add(((float)okNorm / counted, ln, hits));
            }
            results.Sort((a, b) => b.score.CompareTo(a.score));
            best = results[0].l;
            float second = results.Count > 1 ? results[1].score : 0f;
            report = $"Best {results[0].score:P0} ({results[0].l}, {results[0].hits}/{cells.Count} cells over something), next {second:P0}; "
                   + $"map values {vMin:0.00}..{vMax:0.00}, scan layers 0x{mask:X}.";
            // Confident only if it clearly fits and clearly beats the other layouts.
            return results[0].score >= 0.6f && results[0].score - second >= 0.1f;
        }

        // Rain effects that take the height map.
        private static void FindTargets()
        {
            _targets.Clear();
            var found = new List<string>();
            foreach (var vfx in Resources.FindObjectsOfTypeAll<VisualEffect>())
            {
                if (vfx == null || !vfx.gameObject.scene.IsValid()) continue;
                foreach (var n in BufferNames)
                    if (vfx.HasGraphicsBuffer(n)) { _targets.Add((vfx, n, true)); found.Add($"{vfx.name}.{n}"); }
                foreach (var n in TextureNames)
                    if (vfx.HasTexture(n)) { _targets.Add((vfx, n, false)); found.Add($"{vfx.name}.{n}"); }
            }
            Core.Log.Msg(found.Count > 0
                ? "Rain effect inputs updated with the dome: " + string.Join(", ", found)
                : "No rain effect takes a height map input - the game's rain CPU checks are updated, but the drops may still show inside.");
        }

        // Hand the stamped map to the rain effects.
        private static void Push()
        {
            if (_targets.Count == 0) return;
            if (_mask == null) _mask = Object.FindObjectOfType<MaskController>(); // once, not every 3 s
            var mc = _mask;
            if (mc == null) return;
            var map = mc.HeightMap;
            int res = mc.HeightMapResolution;
            var mm = mc.MinMaxHeight;
            if (map == null) return;

            bool needBuffer = false, needTexture = false;
            foreach (var t in _targets) if (t.isBuffer) needBuffer = true; else needTexture = true;

            if (needBuffer && _buffer == null)
            {
                _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, map.Length, 4);
                // InternalSetData: SetData needs the stripped Il2CppSystem.Object.GetType.
                _buffer.InternalSetData(new Il2CppSystem.Array(map.Pointer), 0, 0, map.Length, 4);
            }
            if (needTexture && _texture == null)
            {
                // Same layout as the grid (row after row), values 0-1 like the game's baked mask.
                _texture = new Texture2D(res, res, TextureFormat.RFloat, false, true) { name = "HeightMask (with Greenhouse Island)" };
                var bytes = new Il2CppStructArray<byte>(res * res * 4);
                for (int k = 0; k < res * res; k++)
                {
                    float v = _layout.Normalised ? map[k] : Mathf.InverseLerp(mm.x, mm.y, map[k]);
                    var b = System.BitConverter.GetBytes(v);
                    int at = k * 4;
                    bytes[at] = b[0]; bytes[at + 1] = b[1]; bytes[at + 2] = b[2]; bytes[at + 3] = b[3];
                }
                _texture.LoadRawTextureData(bytes);
                _texture.Apply(false, false);
            }
            foreach (var (vfx, name, isBuffer) in _targets)
            {
                if (vfx == null) continue;
                if (isBuffer) vfx.SetGraphicsBuffer(name, _buffer);
                else vfx.SetTexture(name, _texture);
            }
        }
    }
}

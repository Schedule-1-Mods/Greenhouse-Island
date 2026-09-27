using System.Collections.Generic;
using Il2CppScheduleOne.Map;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Where Greenhouse Island sits: the floating platform's centre, plus the points on the
    // shore where boat docks are built (each gets a gangway down to a floating landing with its
    // own boat). The platform turns its door towards the nearest dock.
    internal sealed class Site
    {
        public Vector3 Center;
        public Vector3 Dir;          // platform "forward": from the nearest dock towards the centre
        public Vector3 Right;
        public float SeaLevel;
        public float IslandTop;      // deck height
        public float Radius;
        public readonly List<Vector3> Docks = new List<Vector3>();
        // Filled in by the builder: the water-side end of each dock, for the water-warp zone.
        public readonly List<(Vector3 from, Vector3 to)> DockSpans = new List<(Vector3, Vector3)>();

        // The floating platform's deck sits this far above the water.
        public const float DeckAboveSea = 1.0f;

        // First dock (or the centre if there are none) - the "mainland" end.
        public Vector3 Anchor => Docks.Count > 0 ? Docks[0] : Center;

        public static Site Create(Vector3 center, IEnumerable<Vector3> docks)
        {
            var s = new Site
            {
                SeaLevel = Siting.SeaLevel(),
                Radius = IslandBuilder.PlatformRadius,
            };
            s.IslandTop = s.SeaLevel + DeckAboveSea;
            s.Center = new Vector3(center.x, s.IslandTop, center.z);
            if (docks != null) s.Docks.AddRange(docks);

            Vector3 dir = Vector3.forward;
            float best = float.MaxValue;
            foreach (var d in s.Docks)
            {
                var v = s.Center - d; v.y = 0f;
                if (v.sqrMagnitude > 1f && v.sqrMagnitude < best) { best = v.sqrMagnitude; dir = v.normalized; }
            }
            s.Dir = dir;
            s.Right = Vector3.Cross(Vector3.up, dir);
            return s;
        }

        // Platform-local (x right, z away from the nearest dock, y above the deck) -> world.
        public Vector3 Local(float x, float y, float z) => Center + Right * x + Vector3.up * y + Dir * z;

        // True for anything standing on the platform or a dock - used to stop the game's
        // "fell in the sea" trigger warping people off our (above-water) ground.
        public bool InZone(Vector3 p)
        {
            if (p.y < SeaLevel + 0.4f) return false;
            Vector2 flat = new Vector2(p.x - Center.x, p.z - Center.z);
            if (flat.magnitude < Radius + 4f) return true;

            foreach (var (a, b) in DockSpans)
            {
                Vector2 ab = new Vector2(b.x - a.x, b.z - a.z);
                Vector2 ap = new Vector2(p.x - a.x, p.z - a.z);
                float t = ab.sqrMagnitude < 0.01f ? 0f : Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude);
                if ((ap - ab * t).magnitude < 6f) return true;
            }
            return false;
        }
    }

    internal static class Siting
    {
        private static float? _sea;

        public static void ResetCaches() => _sea = null;

        // The sea's surface height: the biggest water-shaded plane in the scene, else the top of
        // the game's water trigger volumes, else 0.
        public static float SeaLevel()
        {
            if (_sea.HasValue) return _sea.Value;

            var water = FindOcean();
            if (water != null)
            {
                _sea = water.bounds.center.y;
            }
            else
            {
                float best = float.NegativeInfinity;
                foreach (var wc in Object.FindObjectsOfType<WaterCollider>())
                    foreach (var c in wc.GetComponents<Collider>())
                        best = Mathf.Max(best, c.bounds.max.y);
                _sea = float.IsNegativeInfinity(best) ? 0f : best;
            }
            Core.Log.Msg($"Sea level: {_sea.Value:0.00}");
            return _sea.Value;
        }

        public static MeshRenderer FindOcean()
        {
            MeshRenderer best = null;
            float bestArea = 100f * 100f;
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                var m = r.sharedMaterial;
                if (m == null || m.shader == null) continue;
                string n = m.shader.name.ToLowerInvariant() + " " + m.name.ToLowerInvariant();
                if (!n.Contains("water")) continue;
                var b = r.bounds;
                float area = b.size.x * b.size.z;
                if (area > bestArea && b.size.y < 5f)
                {
                    bestArea = area;
                    best = r;
                }
            }
            return best;
        }

        // Height of solid, visible ground at (x, z). Skips triggers, physics props/vehicles and
        // invisible blocking walls, so the map-edge walls don't read as land.
        public static float? GroundHeight(float x, float z, Transform ignore)
        {
            var hits = Physics.RaycastAll(new Vector3(x, 600f, z), Vector3.down, 1200f, ~0, QueryTriggerInteraction.Ignore);
            float? best = null;
            foreach (var h in hits)
            {
                var c = h.collider;
                if (c == null || c.attachedRigidbody != null) continue;
                if (ignore != null && c.transform.IsChildOf(ignore)) continue;
                if (!IsVisibleSolid(c)) continue;
                if (!best.HasValue || h.point.y > best.Value) best = h.point.y;
            }
            return best;
        }

        public static bool IsTerrain(Collider c) => c.GetIl2CppType().Name == "TerrainCollider";

        public static bool IsVisibleSolid(Collider c)
        {
            if (IsTerrain(c)) return true;
            return c.GetComponent<Renderer>() != null || c.GetComponentInChildren<Renderer>() != null;
        }

        private static Vector3 MapCenter()
        {
            var terrains = Terrain.activeTerrains;
            if (terrains != null && terrains.Length > 0)
            {
                Bounds b = default;
                bool any = false;
                foreach (var t in terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    var tb = new Bounds(t.GetPosition() + t.terrainData.size * 0.5f, t.terrainData.size);
                    if (!any) { b = tb; any = true; } else b.Encapsulate(tb);
                }
                if (any) return new Vector3(b.center.x, 0f, b.center.z);
            }
            return Vector3.zero;
        }

        // Knock out invisible blocking walls (colliders with nothing visible attached) that
        // stand across the bridge or island, so the map-edge barrier doesn't block the way.
        public static int ClearInvisibleWalls(Site site, Transform ours)
        {
            int cleared = 0;
            var seen = new HashSet<System.IntPtr>();
            void Sweep(Vector3 centre, Vector3 halfExtents, Quaternion rot)
            {
                foreach (var c in Physics.OverlapBox(centre, halfExtents, rot, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c == null || !c.enabled || !seen.Add(c.Pointer)) continue;
                    if (c.transform.IsChildOf(ours) || c.attachedRigidbody != null) continue;
                    if (IsTerrain(c) || IsVisibleSolid(c)) continue;
                    if (c.GetIl2CppType().Name == "CharacterController") continue;
                    if (c.bounds.size.y < 2.5f) continue;
                    c.enabled = false;
                    cleared++;
                    Core.Log.Msg($"Opened invisible wall '{c.gameObject.name}' across the way to Greenhouse Island.");
                }
            }

            var rot = Quaternion.LookRotation(site.Dir);
            // Only the walkway paths and the platform itself - the platform can sit right by a busy
            // quay, so anything wider would switch off the game's own invisible colliders there.
            foreach (var dock in site.Docks)
            {
                var to = site.Center - dock; to.y = 0f;
                float len = to.magnitude;
                if (len < 1f) continue;
                var dir = to / len;
                var chanRot = Quaternion.LookRotation(dir);
                for (float t = -6f; t < len; t += 10f)
                {
                    var p = dock + dir * t;
                    Sweep(new Vector3(p.x, site.SeaLevel + 6f, p.z), new Vector3(1.6f, 5f, 5.5f), chanRot);
                }
            }
            float r = site.Radius + 1f;
            Sweep(new Vector3(site.Center.x, site.SeaLevel + 8f, site.Center.z), new Vector3(r, 8f, r), rot);
            return cleared;
        }
    }
}

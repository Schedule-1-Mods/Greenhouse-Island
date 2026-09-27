using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using UnityEngine;
using UnityEngine.AI;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace PelicanKey
{
    // The island is new ground, so the game's baked NavMesh doesn't cover it and employees
    // couldn't walk anywhere. This bakes one at runtime from the island's actual colliders
    // (ground, greenhouse walls, tree trunks, and anything you've built), for every agent type,
    // and re-bakes when items are placed or removed so employees path around them.
    //
    // The walkways are baked in too, and the whole thing is its own navmesh area (IslandArea).
    // Separate navmeshes don't join up, and navmesh links can't be used (the game treats every
    // link as a ladder), so NPCs cross over at a walkway's handover spot on the quay, where this
    // navmesh and the game's overlap: switch their area mask there and carry on walking.
    internal static class IslandNavMesh
    {
        public const int IslandArea = 29;
        private sealed class Baked
        {
            public int AgentTypeId;
            public NavMeshData Data;
            public NavMeshDataInstance Instance;
        }

        private static readonly List<Baked> _baked = new List<Baked>();
        // Baked in the island's own (rotated) frame so the box can hug the bridge and island
        // without also covering a big square of mainland; _worldBounds encloses it for collection.
        private static Vector3 _pos;
        private static Quaternion _rot;
        private static Bounds _bounds;
        private static Bounds _worldBounds;
        private static int _mask;
        private static int _lastItemCount = -1;
        private static float _nextCheck;

        public static void Clear()
        {
            foreach (var b in _baked)
            {
                try { if (b.Instance.valid) b.Instance.Remove(); } catch { }
            }
            _baked.Clear();
            _lastItemCount = -1;
        }

        public static void Bake(Site site)
        {
            Clear();
            try
            {
                // Just the floating platform: its deck ring and the dome.
                float half = site.Radius + 4f;
                float zMin = -site.Radius - 4f;
                float zMax = site.Radius + 4f;
                _pos = site.Center;
                _rot = Quaternion.LookRotation(site.Dir);
                float lowY = Mathf.Min(site.Anchor.y - site.IslandTop, -4f) - 4f;
                float highY = Mathf.Max(site.Anchor.y - site.IslandTop, 0f) + 20f;
                _bounds = new Bounds(new Vector3(0f, (lowY + highY) * 0.5f, (zMin + zMax) * 0.5f),
                                     new Vector3(half * 2f, highY - lowY, zMax - zMin));
                // ...and out along every walkway to a few metres past its handover spot on land.
                var inverse = Quaternion.Inverse(_rot);
                foreach (var dock in IslandBuilder.Docks)
                {
                    var local = inverse * (dock.Portal - _pos);
                    _bounds.Encapsulate(new Bounds(local, new Vector3(8f, 8f, 8f)));
                }
                _worldBounds = WorldAabb(_bounds, _pos, _rot);
                _mask = WalkableMask();
                var sources = CollectSources();

                var seen = new HashSet<int>();
                for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
                {
                    var settings = NavMesh.GetSettingsByIndex(i);
                    if (!seen.Add(settings.agentTypeID)) continue;
                    var data = NavMeshBuilder.BuildNavMeshData(settings, sources, _bounds, _pos, _rot);
                    if (data == null) continue;
                    var inst = NavMesh.AddNavMeshData(data, _pos, _rot);
                    if (!inst.valid) continue;
                    _baked.Add(new Baked { AgentTypeId = settings.agentTypeID, Data = data, Instance = inst });
                }
                NavMeshUtility.ClearCache();
                Core.Log.Msg($"Island navmesh baked for {_baked.Count} agent type(s) from {sources.Count} colliders.");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't bake the island navmesh - employees won't be able to walk there: " + e.Message);
            }
        }

        // Re-bake a few seconds after the number of items in the greenhouse changes.
        public static void Tick()
        {
            if (_baked.Count == 0 || Time.time < _nextCheck) return;
            _nextCheck = Time.time + 3f;

            var prop = JungleProperty.Property;
            if (prop == null) return;
            int count;
            try { count = prop.BuildableItems.Count; } catch { return; }
            if (_lastItemCount == -1) { _lastItemCount = count; return; }
            if (count == _lastItemCount) return;
            _lastItemCount = count;

            try
            {
                var sources = CollectSources();
                foreach (var b in _baked)
                    NavMeshBuilder.UpdateNavMeshData(b.Data, NavMesh.GetSettingsByID(b.AgentTypeId), sources, _bounds);
                NavMeshUtility.ClearCache();
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Island navmesh update failed: " + e.Message);
            }
        }

        // Everything solid on the platform (the island, trees, what you've built), plus the
        // island's own parts anywhere in the box - the walkways - but not the mainland under and
        // around their shore ends: that's the game's navmesh already.
        private static Il2CppList.List<NavMeshBuildSource> CollectSources()
        {
            var all = new Il2CppList.List<NavMeshBuildSource>();
            var markups = new Il2CppList.List<NavMeshBuildMarkup>();
            NavMeshBuilder.CollectSources(_worldBounds, _mask, NavMeshCollectGeometry.PhysicsColliders, IslandArea, markups, all);

            var root = IslandBuilder.Root;
            float reach = (IslandBuilder.Current?.Site?.Radius ?? IslandBuilder.PlatformRadius) + 4f;
            var kept = new Il2CppList.List<NavMeshBuildSource>();
            for (int i = 0; i < all.Count; i++)
            {
                var src = all[i];
                var comp = src.component;
                if (comp == null) continue;
                bool ours = root != null && comp.transform.IsChildOf(root);
                if (!ours)
                {
                    var col = comp.TryCast<Collider>();
                    if (col == null) continue; // terrain etc.: mainland
                    var c = col.bounds.center - _pos;
                    c.y = 0f;
                    if (c.magnitude > reach) continue;
                }
                src.area = IslandArea;
                kept.Add(src);
            }
            return kept;
        }

        private static Bounds WorldAabb(Bounds local, Vector3 pos, Quaternion rot)
        {
            var world = new Bounds(pos + rot * local.center, Vector3.zero);
            var e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = local.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                world.Encapsulate(pos + rot * corner);
            }
            return world;
        }

        // Everything except layers that aren't real obstacles or floor (property bounds, build
        // tiles, and moving characters/vehicles).
        private static int WalkableMask()
        {
            int mask = ~0;
            foreach (var name in new[] { "Invisible", "Tile", "Player", "NPC", "Vehicle", "Ignore Raycast", "Water", "UI" })
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask &= ~(1 << layer);
            }
            return mask;
        }
    }
}

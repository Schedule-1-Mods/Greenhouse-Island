using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using UnityEngine;
using UnityEngine.AI;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace PelicanKey
{
    // The island is new ground, so the game's baked NavMesh doesn't cover it and employees
    // couldn't walk anywhere. This bakes one at runtime from the island's actual colliders
    // (ground, greenhouse walls, tree trunks - but not what you've placed, just like the game's
    // own navmeshes), for every agent type.
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
        private static bool _async = true;

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
            LetEmployeesOn(prop);
            int count;
            try { count = prop.BuildableItems.Count; } catch { return; }
            if (_lastItemCount == -1) { _lastItemCount = count; return; }
            if (count == _lastItemCount) return;
            _lastItemCount = count;

            try
            {
                var sources = CollectSources();
                foreach (var b in _baked)
                {
                    // In the background (no frame hitch when you place something); the old
                    // all-at-once rebuild if this game build lacks the async version.
                    if (_async)
                    {
                        try { NavMeshBuilder.UpdateNavMeshDataAsync(b.Data, NavMesh.GetSettingsByID(b.AgentTypeId), sources, _bounds); continue; }
                        catch { _async = false; }
                    }
                    NavMeshBuilder.UpdateNavMeshData(b.Data, NavMesh.GetSettingsByID(b.AgentTypeId), sources, _bounds);
                }
                NavMeshUtility.ClearCache();
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Island navmesh update failed: " + e.Message);
            }
        }

        // The island's employees need its area in their walkable mask, or the game can't put them
        // on its navmesh at all: they stand still, and every behaviour tick the game retries and
        // logs "Failed to create agent" - thousands of times, which tanks the frame rate. Add the
        // island area to their mask (keeping the game's own areas) and set them back down.
        private static void LetEmployeesOn(Il2CppScheduleOne.Property.Property prop)
        {
            const int islandBit = 1 << IslandArea;
            int fixedCount = 0;
            try
            {
                foreach (var emp in prop.Employees)
                {
                    var agent = emp?.Movement?.Agent;
                    if (agent == null || (agent.areaMask & islandBit) != 0) continue;
                    agent.areaMask |= islandBit;
                    if (!agent.isOnNavMesh) Compat.Warp(emp.Movement, emp.transform.position);
                    fixedCount++;
                }
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't let the island's employees onto its navmesh: " + e.Message);
            }
            if (fixedCount > 0) Core.Log.Msg($"Let {fixedCount} employee(s) walk on the island's navmesh.");
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
                // Nothing you've placed (pots, racks, lockers, stations...): the game's own
                // navmeshes are baked without them too, and its employees need to walk right up
                // to each item's access point (within 1 m) to use it. Cutting placed items out of
                // the walkable area left those spots off it, so every job failed its "can I get
                // there" check and the employee gave up and went to wait outside.
                if (comp.GetComponentInParent<Il2CppScheduleOne.EntityFramework.BuildableItem>() != null) continue;
                bool ours = root != null && comp.transform.IsChildOf(root);
                if (!ours)
                {
                    var col = comp.TryCast<Collider>();
                    if (col == null) continue; // terrain etc.: mainland
                    var c = col.bounds.center - _pos;
                    c.y = 0f;
                    if (c.magnitude > reach) continue;
                }
                // The weed plant and its planter (the ring of soil round its trunk) are not for
                // walking on: marked "Not Walkable" (area 1), so employees are never sent up onto
                // them, where they could end up wedged against the trunk.
                bool plantZone = false;
                for (var t = comp.transform; t != null && t != root; t = t.parent)
                    if (t.name == "WeedPlant") { plantZone = true; break; }
                src.area = plantZone ? 1 : IslandArea;
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

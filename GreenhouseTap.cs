using System.Collections.Generic;
using Il2CppFishNet;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.Property;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // A water tap in the dome, on the base wall behind the stairs, for filling watering cans.
    // It's a copy of the game's own tap (ScheduleOne.Property.Tap, the one at other properties):
    // hold a watering can, "E  Use tap", and turn the handle - water, sound and all.
    //
    // The tap is a networked object (its flow and "in use by" state go through the game's
    // networking), so the copy gets a network object of its own and is spawned, like the island
    // property itself. Parts of the original that were merged into the level's combined meshes
    // are cut back out (see BatchedMesh) so the copy doesn't bring a whole area of scenery with it.
    internal static class GreenhouseTap
    {
        // Island-local spot beside the foot of the stairs (they run along the dome wall at
        // x = -9.6 from z = -2.9 up to z = 2.9, 2.5 m wide), just before the first step; the tap goes on the wall
        // straight out from here.
        private static readonly Vector3 UnderStairs = new Vector3(-10.95f, IslandBuilder.GhFloor + 0.9f, -3.9f);

        public static void Create(Transform island, System.Action<NetworkObject> spawn)
        {
            if (island == null) return;
            try
            {
                Tap template = null;
                foreach (var t in Resources.FindObjectsOfTypeAll<Tap>())
                {
                    if (t == null || !t.gameObject.scene.IsValid() || !t.gameObject.activeInHierarchy) continue;
                    template = t;
                    break;
                }
                if (template == null) { Core.Log.Warning("No tap found in the game to copy - the greenhouse won't have one."); return; }

                // Copy the smallest object holding all the tap's working parts (its E box, handle,
                // water, sounds, camera spot) so the copy's references point at its own parts.
                var needed = new List<Transform>();
                foreach (var c in new Component[] { template._interactable, template._handleClickable, template._waterParticles, template._squeakSound, template._waterRunningSound })
                    if (c != null) needed.Add(c.transform);
                foreach (var t in new[] { template._handleTransform, template.CameraPos, template.FillableModelContainer, template._handlePromptAnchor })
                    if (t != null) needed.Add(t);
                var src = template.transform;
                for (int up = 0; up < 3 && needed.Exists(t => !t.IsChildOf(src)) && src.parent != null; up++) src = src.parent;
                if (needed.Exists(t => !t.IsChildOf(src))) { Core.Log.Warning("The game's tap is spread over the scene - couldn't copy it."); return; }
                // (Only when that meant taking a parent along: it mustn't be a whole building. Sized
                // by colliders - renderers merged into the level's combined meshes report the
                // combined mesh's size, and particles can be huge.)
                if (src != template.transform)
                {
                    Bounds size = new Bounds(src.position, Vector3.zero);
                    foreach (var c in src.GetComponentsInChildren<Collider>(true)) size.Encapsulate(c.bounds);
                    if (size.size.magnitude > 3f) { Core.Log.Warning($"The game's tap comes as part of '{src.name}' ({size.size.magnitude:0.0} m) - too big to copy."); return; }
                }

                // How the original stands: which way its back (the wall) is, how far off the wall
                // and how high above the floor it is.
                var ignore = new HashSet<System.IntPtr>();
                foreach (var c in src.GetComponentsInChildren<Collider>(true)) ignore.Add(c.Pointer);
                Vector3 centre = src.position;
                Vector3 back = -src.forward; float backDist = 0.1f; float nearest = float.MaxValue;
                for (int a = 0; a < 360; a += 15)
                {
                    var d = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
                    foreach (var hit in Physics.RaycastAll(centre, d, 1.5f, ~0, QueryTriggerInteraction.Ignore))
                        if (hit.collider != null && !ignore.Contains(hit.collider.Pointer) && hit.collider.bounds.size.magnitude > 1f && hit.distance < nearest)
                        { nearest = hit.distance; back = d; backDist = hit.distance; }
                }
                float height = 0.9f;
                foreach (var hit in Physics.RaycastAll(centre + Vector3.up * 0.05f, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore))
                    if (hit.collider != null && !ignore.Contains(hit.collider.Pointer) && centre.y - hit.point.y < height + 2f) { height = centre.y - hit.point.y; break; }

                // Where it goes: on the floor against the dome's base wall, just left of the main
                // door (as you face out) - see IslandBuilder.TapWallPoint.
                Vector3 wallPoint, wallNormal;
                if (IslandBuilder.TapWallSet) { wallPoint = IslandBuilder.TapWallPoint; wallNormal = IslandBuilder.TapWallNormal; }
                else
                {
                    var from = island.TransformPoint(UnderStairs);
                    var outward = island.TransformDirection(new Vector3(-1f, 0f, 0f));
                    wallPoint = from + outward * 1.3f; wallNormal = -outward;
                    float best = float.MaxValue;
                    foreach (var hit in Physics.RaycastAll(from, outward, 3f, ~0, QueryTriggerInteraction.Ignore))
                        if (hit.collider != null && hit.collider.transform.IsChildOf(island) && hit.distance < best)
                        { best = hit.distance; wallPoint = hit.point; wallNormal = hit.normal; }
                }
                wallNormal.y = 0f; wallNormal.Normalize();

                // The copy, inactive until it's ready; posed where the original is for now.
                var holder = new GameObject("TapHolder");
                holder.SetActive(false);
                var copy = Object.Instantiate(src.gameObject, holder.transform, true);
                copy.name = "GreenhouseTap";

                // The pieces. In the game they're driven by level-of-detail groups (the tap's own,
                // plus the building's it stands in): when you're far from that building the game
                // switches them off, so a copy taken while away comes out with pieces missing.
                // The copy needs no LOD groups (it's one small tap): they go, and every piece is
                // switched on except the lower-detail versions.
                //
                // Pieces merged into the level's big combined meshes (static-batched) are cut out
                // as parts of their own, whatever their state; the copy's own renderer for those
                // would draw the whole combined mesh, so it's removed.
                var origRs = src.GetComponentsInChildren<MeshRenderer>(true);
                var copyRs = copy.GetComponentsInChildren<MeshRenderer>(true);
                var lowerLods = new HashSet<System.IntPtr>();
                foreach (var lg in src.GetComponentsInChildren<LODGroup>(true))
                {
                    var lods = lg.GetLODs();
                    for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r != null) lowerLods.Add(r.Pointer);
                }
                foreach (var lg in copy.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(lg);
                int cut = 0;
                var seen = new List<string>();
                for (int i = 0; i < origRs.Length && i < copyRs.Length; i++)
                {
                    var o = origRs[i];
                    if (o == null) continue;
                    // ("Low detail" in the game's tap LOD group turned out to be its pipe, spout and bend -
                    // the tap has no other versions of them - so nothing is left out.)
                    bool lower = false;
                    bool tagged = lowerLods.Contains(o.Pointer);
                    seen.Add($"{o.name}{(o.isPartOfStaticBatch ? " [merged]" : "")}{(o.enabled ? "" : " [was off]")}{(tagged ? " [low detail]" : "")}");
                    var go = copyRs[i].gameObject;
                    if (o.isPartOfStaticBatch)
                    {
                        var mf = go.GetComponent<MeshFilter>();
                        Object.DestroyImmediate(copyRs[i]);
                        if (mf != null) Object.DestroyImmediate(mf);
                        if (lower) continue;
                        var mesh = BatchedMesh.Extract(o, out _);
                        if (mesh == null) continue;
                        var part = new GameObject(o.name + " (part)");
                        part.transform.SetPositionAndRotation(o.bounds.center, Quaternion.identity);
                        part.transform.SetParent(go.transform, true);
                        part.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var mr = part.AddComponent<MeshRenderer>();
                        mr.sharedMaterials = o.sharedMaterials;
                        mr.shadowCastingMode = o.shadowCastingMode;
                        part.layer = o.gameObject.layer;
                        cut++;
                    }
                    else copyRs[i].enabled = !lower;
                }
                Core.Log.Msg($"Greenhouse tap pieces: {string.Join(", ", seen)}.");

                // Its own network identity (not the original's scene one).
                foreach (var no in copy.GetComponentsInChildren<NetworkObject>(true)) Object.DestroyImmediate(no);
                var netObj = copy.AddComponent<NetworkObject>();

                // Onto the wall: turned so its back faces the wall, as far off it and as high
                // above the floor as the original.
                var turn = Quaternion.FromToRotation(new Vector3(back.x, 0f, back.z).normalized, -wallNormal);
                copy.transform.rotation = turn * copy.transform.rotation;
                var floorY = island.TransformPoint(new Vector3(0f, IslandBuilder.GhFloor, 0f)).y;
                var pos = wallPoint + wallNormal * backDist;
                copy.transform.position = new Vector3(pos.x, floorY + Mathf.Max(0f, height), pos.z);
                // Fully built while switched off, then on (like the island property itself), so
                // its network parts wake up with everything in place.
                copy.SetActive(false);
                copy.transform.SetParent(island, true);
                Object.Destroy(holder);

                copy.SetActive(true);
                byte index = 0;
                netObj.UpdateNetworkBehaviours(null, ref index);
                var tap = copy.GetComponentInChildren<Tap>(true);
                if (tap != null) tap.NetworkInitializeIfDisabled();
                spawn(netObj);

                if (tap != null)
                {
                    var box = tap._interactable != null ? tap._interactable.GetComponentInChildren<Collider>(true) : null;
                    var handle = tap._handleClickable != null ? tap._handleClickable.GetComponentInChildren<Collider>(true) : null;
                    Core.Log.Msg($"Greenhouse tap: E box {(box != null ? $"on layer {LayerMask.LayerToName(box.gameObject.layer)}, {box.bounds.size.x:0.0}x{box.bounds.size.y:0.0}x{box.bounds.size.z:0.0} m, enabled {box.enabled}" : "MISSING")}; " +
                                 $"handle {(handle != null ? $"collider enabled {handle.enabled}" : "collider MISSING")}; at {copy.transform.position}.");
                }
                Core.Log.Msg($"Greenhouse tap copied from '{template.name}' ({cut} merged part(s) cut out), {height:0.00} m up on the wall behind the stairs" +
                             (netObj.IsSpawned ? "." : " - but it didn't spawn on the network, so it may not fill cans."));
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't add the greenhouse tap: " + e.Message);
            }
        }
    }
}

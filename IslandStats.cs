using System.Collections.Generic;
using UnityEngine;

namespace PelicanKey
{
    // One line in the log, once the island is built, saying how much there is to draw: how many
    // objects, renderers, shadow casters, see-through surfaces, triangles, lights and colliders.
    // Frame time follows these numbers, so this is where to look first when something is slow.
    internal static class IslandStats
    {
        public static void Log(Transform root)
        {
            try
            {
                int all = root.GetComponentsInChildren<Transform>(true).Length;
                var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                int shadowCasters = 0, transparent = 0, tiny = 0;
                long triangles = 0;
                var mats = new HashSet<System.IntPtr>();
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) shadowCasters++;
                    if (r.bounds.size.magnitude < 0.5f) tiny++;
                    var m = r.sharedMaterial;
                    if (m != null) { mats.Add(m.Pointer); if (m.renderQueue >= 3000) transparent++; }
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh != null) for (int i = 0; i < mesh.subMeshCount; i++) triangles += mesh.GetIndexCount(i) / 3;
                }
                int colliders = root.GetComponentsInChildren<Collider>(true).Length;
                int lights = root.GetComponentsInChildren<Light>(true).Length;
                // Which part of the island is the heaviest: by top-level child.
                var parts = new List<(string name, int renderers, long tris, int casters)>();
                var level = new List<Transform>();
                for (int c = 0; c < root.childCount; c++)
                {
                    var top1 = root.GetChild(c);
                    void Expand(Transform t) { for (int d = 0; d < t.childCount; d++) { var k = t.GetChild(d); if (k.name == "Greenhouse" || k.name == "Platform") Expand(k); else level.Add(k); } }
                    if (top1.name == "Island") Expand(top1); else level.Add(top1);
                }
                var byName = new Dictionary<string, (int renderers, long tris, int casters, int count)>();
                foreach (var child in level)
                {
                    int rn = 0, cs = 0; long tr = 0;
                    foreach (var r in child.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (r == null) continue;
                        rn++;
                        if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) cs++;
                        var mf2 = r.GetComponent<MeshFilter>(); var mesh2 = mf2 != null ? mf2.sharedMesh : null;
                        if (mesh2 != null) for (int i = 0; i < mesh2.subMeshCount; i++) tr += mesh2.GetIndexCount(i) / 3;
                    }
                    byName.TryGetValue(child.name, out var agg);
                    byName[child.name] = (agg.renderers + rn, agg.tris + tr, agg.casters + cs, agg.count + 1);
                }
                foreach (var kv in byName) parts.Add((kv.Key + (kv.Value.count > 1 ? " x" + kv.Value.count : ""), kv.Value.renderers, kv.Value.tris, kv.Value.casters));
                parts.Sort((a, b) => b.renderers.CompareTo(a.renderers));
                var top = new List<string>();
                for (int i = 0; i < parts.Count && i < 30; i++) top.Add($"{parts[i].name}: {parts[i].renderers} renderers, {parts[i].tris / 1000} k tris, {parts[i].casters} shadow");
                Core.Log.Msg("[Stats] Heaviest parts - " + string.Join("; ", top));
                Core.Log.Msg($"[Stats] The island has {all} objects: {renderers.Length} renderers ({shadowCasters} cast shadows, {transparent} see-through, {tiny} under half a metre), " +
                             $"{mats.Count} materials, {triangles / 1000} k triangles, {lights} lights, {colliders} colliders.");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("[Stats] " + e.Message);
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Drawing cost. Everything the island builds out of primitives - every dome rib, beam, rail,
    // baluster, tread and lamp post - is its own object with its own renderer that casts its own
    // shadow, and each one is a separate draw call (several times over, for the shadow cascades).
    // That was over 1,600 renderers and 1,400 shadow casters: some 8 ms of every frame in sight of
    // the island. Here the still pieces are merged: one mesh per material per region of about
    // 6 m (so a region off screen is still skipped), the pieces' colliders stay as they are, and
    // what's left small enough not to be missed in a shadow stops casting one.
    //
    // Left alone: anything that moves or is looked after by other code - the doors, the turbine,
    // the save intercom, the tap, the trees, and the weed plant (merged on its own, below).
    internal static class IslandOptimiser
    {
        private static readonly HashSet<string> Skip = new HashSet<string>
        {
            "GameTree", "DoorPanel", "SaveMount", "SavePointGroup", "WindTurbine", "GreenhouseTap", "TapHolder", "WeedPlant", "Intercom", "SavePoint"
        };

        private const float Cell = 6f;

        public static void Run(Transform root)
        {
            try
            {
                int before = CountRenderers(root);
                int merged = 0, meshes = 0;

                // The still scenery: the platform, the dome (all its floors) and each walkway.
                var still = new List<MeshRenderer>();
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                    if (r != null && r.enabled && !IsSkipped(r.transform, root)) still.Add(r);
                merged += CombineInto(still, root, "Merged", out meshes);

                // The weed plant: its own mesh per material (it is recoloured through those
                // materials, which the merged mesh shares, so the colour menu still works).
                var plant = FindByName(root, "WeedPlant");
                int plantMeshes = 0, plantMerged = 0;
                if (plant != null)
                {
                    var parts = new List<MeshRenderer>();
                    foreach (var r in plant.GetComponentsInChildren<MeshRenderer>(true))
                        if (r != null && r.enabled && r.gameObject.activeInHierarchy) parts.Add(r);
                    plantMerged = CombineInto(parts, plant, "MergedPlant", out plantMeshes);
                }

                // What's left and small: no shadow.
                int noShadow = 0;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r == null || r.shadowCastingMode == ShadowCastingMode.Off) continue;
                    if (r.bounds.size.magnitude < 0.5f) { r.shadowCastingMode = ShadowCastingMode.Off; noShadow++; }
                }

                Core.Log.Msg($"[Optimise] {before} renderers -> {CountRenderers(root)}: merged {merged} still pieces into {meshes} meshes and {plantMerged} plant pieces into {plantMeshes}; {noShadow} small ones stopped casting shadows.");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("[Optimise] " + e);
            }
        }

        private static int CountRenderers(Transform root) => root.GetComponentsInChildren<MeshRenderer>(true).Length;

        // Is this part of something that moves or is handled elsewhere? (any ancestor up to the root)
        private static bool IsSkipped(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
            {
                string n = t.name;
                if (Skip.Contains(n)) return true;
                if (n.StartsWith("GameTree")) return true;
            }
            return false;
        }

        private static Transform FindByName(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        // Merges the renderers' meshes into one mesh per (material, region) under `into`, and
        // removes the renderers (and their mesh filters) - the objects themselves stay.
        private static int CombineInto(List<MeshRenderer> renderers, Transform into, string name, out int meshesMade)
        {
            meshesMade = 0;
            var toLocal = into.worldToLocalMatrix;
            var groups = new Dictionary<(System.IntPtr mat, int x, int y, int z), (Material mat, List<CombineInstance> parts)>();
            var used = new List<MeshRenderer>();
            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || !mesh.isReadable || r.isPartOfStaticBatch) continue;
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;
                var c = r.bounds.center;
                int cx = Mathf.FloorToInt(c.x / Cell), cy = Mathf.FloorToInt(c.y / Cell), cz = Mathf.FloorToInt(c.z / Cell);
                bool any = false;
                for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                {
                    var m = mats[s];
                    if (m == null) continue;
                    var key = (m.Pointer, cx, cy, cz);
                    if (!groups.TryGetValue(key, out var g)) { g = (m, new List<CombineInstance>()); groups[key] = g; }
                    g.parts.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = toLocal * r.transform.localToWorldMatrix });
                    any = true;
                }
                if (any) used.Add(r);
            }

            foreach (var g in groups.Values)
            {
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<CombineInstance>(g.parts.Count);
                for (int i = 0; i < g.parts.Count; i++) arr[i] = g.parts[i];
                var mesh = new Mesh { name = name + "_" + g.mat.name, indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(arr, true, true);
                mesh.RecalculateBounds();
                var go = Kit.MeshObject(into, name, mesh, new[] { g.mat }, false);
                go.isStatic = false;
                meshesMade++;
            }

            foreach (var r in used)
            {
                var mf = r.GetComponent<MeshFilter>();
                Object.Destroy(r);
                if (mf != null) Object.Destroy(mf);
            }
            return used.Count;
        }
    }
}

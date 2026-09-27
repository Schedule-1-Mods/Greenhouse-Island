using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace PelicanKey
{
    // The game's level is static-batched: at build time every static prop (walls, signs, the save
    // intercoms...) was merged into big "Combined Mesh" objects, one per area, with the vertices
    // already in world space. A batched renderer's MeshFilter points at that whole combined mesh,
    // and the renderer only draws its own slice of it (subMeshStartIndex, one sub-mesh per
    // material). So copying the MeshFilter's mesh copies hundreds of metres of scenery, and moving
    // a copy of the object does nothing (its vertices are fixed in the world).
    //
    // This cuts one renderer's slice back out into a mesh of its own, centred on the object, in
    // world orientation - read from the CPU copy if there is one, otherwise from the GPU buffers.
    internal static class BatchedMesh
    {
        public static Mesh Extract(MeshRenderer r, out string how)
        {
            how = "";
            var mf = r.GetComponent<MeshFilter>();
            var src = mf != null ? mf.sharedMesh : null;
            if (src == null) { how = "no mesh"; return null; }

            int first = r.isPartOfStaticBatch ? r.subMeshStartIndex : 0;
            int count = Mathf.Max(1, r.sharedMaterials.Length);
            if (first + count > src.subMeshCount) count = src.subMeshCount - first;
            if (count <= 0) { how = $"sub-meshes {first}.. not in a mesh of {src.subMeshCount}"; return null; }

            Vector3[] pos; Vector3[] nrm; Vector2[] uv; List<int[]> tris;
            if (src.isReadable)
            {
                pos = src.vertices; nrm = src.normals; uv = src.uv;
                tris = new List<int[]>();
                for (int s = first; s < first + count; s++) tris.Add(src.GetTriangles(s, true));
                how = "CPU";
            }
            else if (!ReadGpu(src, first, count, out pos, out nrm, out uv, out tris, out how))
                return null;

            // Batched vertices are in world space (or relative to the batch root, if it has one);
            // an ordinary mesh's are local to the object.
            var root = r.isPartOfStaticBatch ? r.staticBatchRootTransform : r.transform;
            var center = r.bounds.center;

            var map = new Dictionary<int, int>();
            var outPos = new List<Vector3>(); var outNrm = new List<Vector3>(); var outUv = new List<Vector2>();
            var outTris = new List<int[]>();
            foreach (var t in tris)
            {
                var nt = new int[t.Length];
                for (int i = 0; i < t.Length; i++)
                {
                    int v = t[i];
                    if (!map.TryGetValue(v, out int nv))
                    {
                        nv = outPos.Count; map[v] = nv;
                        var p = pos[v];
                        var n = nrm != null && v < nrm.Length ? nrm[v] : Vector3.up;
                        if (root != null) { p = root.TransformPoint(p); n = root.TransformDirection(n); }
                        outPos.Add(p - center);
                        outNrm.Add(n.normalized);
                        outUv.Add(uv != null && v < uv.Length ? uv[v] : Vector2.zero);
                    }
                    nt[i] = nv;
                }
                outTris.Add(nt);
            }
            if (outPos.Count == 0) { how += ", empty slice"; return null; }

            var m = new Mesh { name = r.name + "_extracted" };
            if (outPos.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(ToIl2Cpp(outPos));
            m.SetNormals(ToIl2Cpp(outNrm));
            m.SetUVs(0, ToIl2Cpp(outUv));
            m.subMeshCount = outTris.Count;
            for (int s = 0; s < outTris.Count; s++) m.SetTriangles(outTris[s], s, true);
            m.RecalculateBounds();
            how += $", {outPos.Count} vertices from sub-meshes {first}-{first + count - 1} of '{src.name}'";
            return m;
        }

        private static Il2CppSystem.Collections.Generic.List<T> ToIl2Cpp<T>(List<T> l) where T : unmanaged
        {
            var o = new Il2CppSystem.Collections.Generic.List<T>(l.Count);
            foreach (var x in l) o.Add(x);
            return o;
        }

        private static byte[] Download(GraphicsBuffer b)
        {
            int n = b.count * b.stride;
            var arr = new Il2CppStructArray<byte>(n);
            // InternalGetData, not GetData: the game's build stripped Il2CppSystem.Object.GetType,
            // which GetData calls to size the elements. This goes straight to the engine
            // (n elements of 1 byte).
            b.InternalGetData(new Il2CppSystem.Array(arr.Pointer), 0, 0, n, 1);
            var o = new byte[n];
            for (int i = 0; i < n; i++) o[i] = arr[i];
            return o;
        }

        private static float ReadF(byte[] d, int at, VertexAttributeFormat f) =>
            f == VertexAttributeFormat.Float16 ? Mathf.HalfToFloat(System.BitConverter.ToUInt16(d, at)) : System.BitConverter.ToSingle(d, at);

        private static bool ReadGpu(Mesh src, int first, int count, out Vector3[] pos, out Vector3[] nrm, out Vector2[] uv, out List<int[]> tris, out string how)
        {
            pos = null; nrm = null; uv = null; tris = null; how = "GPU";
            try
            {
                int vc = src.vertexCount;
                var streams = new Dictionary<int, (byte[] data, int stride)>();
                (byte[] data, int stride) Stream(int s)
                {
                    if (streams.TryGetValue(s, out var got)) return got;
                    var vb = src.GetVertexBuffer(s);
                    try { got = (Download(vb), src.GetVertexBufferStride(s)); } finally { vb.Dispose(); }
                    streams[s] = got;
                    return got;
                }

                Vector3[] ReadVec3(VertexAttribute a)
                {
                    if (!src.HasVertexAttribute(a)) return null;
                    var f = src.GetVertexAttributeFormat(a);
                    if (f != VertexAttributeFormat.Float32 && f != VertexAttributeFormat.Float16) return null;
                    int size = f == VertexAttributeFormat.Float16 ? 2 : 4;
                    var (d, stride) = Stream(src.GetVertexAttributeStream(a));
                    int off = src.GetVertexAttributeOffset(a);
                    var o = new Vector3[vc];
                    for (int i = 0; i < vc; i++)
                    {
                        int at = i * stride + off;
                        o[i] = new Vector3(ReadF(d, at, f), ReadF(d, at + size, f), ReadF(d, at + 2 * size, f));
                    }
                    return o;
                }

                pos = ReadVec3(VertexAttribute.Position);
                if (pos == null) { how = "GPU: unreadable positions"; return false; }
                nrm = ReadVec3(VertexAttribute.Normal);
                if (src.HasVertexAttribute(VertexAttribute.TexCoord0))
                {
                    var f = src.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
                    if (f == VertexAttributeFormat.Float32 || f == VertexAttributeFormat.Float16)
                    {
                        int size = f == VertexAttributeFormat.Float16 ? 2 : 4;
                        var (d, stride) = Stream(src.GetVertexAttributeStream(VertexAttribute.TexCoord0));
                        int off = src.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                        uv = new Vector2[vc];
                        for (int i = 0; i < vc; i++) uv[i] = new Vector2(ReadF(d, i * stride + off, f), ReadF(d, i * stride + off + size, f));
                    }
                }

                byte[] ib;
                var b = src.GetIndexBuffer();
                try { ib = Download(b); } finally { b.Dispose(); }
                bool wide = src.indexFormat == IndexFormat.UInt32;
                tris = new List<int[]>();
                for (int s = first; s < first + count; s++)
                {
                    var sd = src.GetSubMesh(s);
                    if (sd.topology != MeshTopology.Triangles) continue;
                    var t = new int[sd.indexCount];
                    for (int i = 0; i < sd.indexCount; i++)
                    {
                        int k = sd.indexStart + i;
                        int idx = wide ? (int)System.BitConverter.ToUInt32(ib, k * 4) : System.BitConverter.ToUInt16(ib, k * 2);
                        t[i] = idx + sd.baseVertex;
                    }
                    tris.Add(t);
                }
                return true;
            }
            catch (System.Exception e)
            {
                how = "GPU read failed: " + e.Message;
                return false;
            }
        }
    }
}

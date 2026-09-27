using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Small toolbox for building scenery at runtime: URP materials, procedural textures,
    // primitives placed in a parent's local space, extruded "ribbon" meshes and 3D text.
    internal static class Kit
    {
        private static Shader _lit;
        private static readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
        private static TMP_FontAsset _font;
        private static bool _fontSearched;

        public static void ResetCaches()
        {
            // Materials/textures are scene-independent, but drop them on rebuild anyway so a
            // tweak to a colour doesn't need a game restart.
            _mats.Clear();
        }

        // ---------------------------------------------------------------- materials

        private static Shader Lit
        {
            get
            {
                if (_lit != null) return _lit;
                _lit = Shader.Find("Universal Render Pipeline/Lit");
                if (_lit == null)
                {
                    // Fall back to whatever lit shader the game's own props use.
                    foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
                    {
                        var m = r.sharedMaterial;
                        if (m != null && m.shader != null && m.shader.name.Contains("Lit"))
                        {
                            _lit = m.shader;
                            break;
                        }
                    }
                }
                if (_lit == null) _lit = Shader.Find("Standard");
                return _lit;
            }
        }

        public static Material Mat(string key, Color colour, float smoothness = 0.15f,
                                   Texture2D tex = null, Vector2? tiling = null, float metallic = 0f)
        {
            if (_mats.TryGetValue(key, out var cached) && cached != null) return cached;

            var m = new Material(Lit) { name = "PelicanKey_" + key };
            m.color = colour;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (tex != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                m.mainTexture = tex;
                if (tiling.HasValue)
                {
                    if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", tiling.Value);
                    m.mainTextureScale = tiling.Value;
                }
            }
            _mats[key] = m;
            return m;
        }

        public static Material Glow(string key, Color colour, float intensity = 2f)
        {
            if (_mats.TryGetValue(key, out var cached) && cached != null) return cached;
            var m = Mat(key, colour, 0.5f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", colour * intensity);
            return m;
        }

        // See-through URP Lit (alpha from colour.a).
        public static Material Glass(string key, Color colour)
        {
            if (_mats.TryGetValue(key, out var cached) && cached != null) return cached;
            var m = Mat(key, colour, 0.92f);
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = 3000;
            return m;
        }

        // ---------------------------------------------------------------- textures

        // Soft mottled noise around a base colour - sand, grass, concrete, asphalt.
        public static Texture2D NoiseTex(Color baseCol, float variation, float scale, int seed)
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float n = TileNoise(x, y, size, scale, seed) * 0.7f + TileNoise(x, y, size, scale * 4f, seed + 17) * 0.3f;
                float k = 1f + (n - 0.5f) * 2f * variation;
                px[y * size + x] = new Color(baseCol.r * k, baseCol.g * k, baseCol.b * k, 1f);
            }
            tex.SetPixels(px);
            tex.Apply(true);
            Sharpen(tex);
            return tex;
        }

        // Timber planks running along V, with dark seams and per-plank tint.
        private static readonly Dictionary<Color, Texture2D> _plankTex = new Dictionary<Color, Texture2D>();
        public static Texture2D PlankTex(Color baseCol)
        {
            if (_plankTex.TryGetValue(baseCol, out var made) && made != null) return made;
            made = MakePlankTex(baseCol);
            _plankTex[baseCol] = made;
            return made;
        }

        private static Texture2D MakePlankTex(Color baseCol)
        {
            const int size = 512;
            const int planks = 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            var px = new Color[size * size];
            var rng = new System.Random(7);
            var tints = new float[planks];
            for (int i = 0; i < planks; i++) tints[i] = 0.85f + (float)rng.NextDouble() * 0.3f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int plank = x * planks / size;
                int inPlank = x % (size / planks);
                float grain = 0.9f + 0.2f * Mathf.PerlinNoise(x * 0.9f * 128f / size, y * 0.06f * 128f / size + plank * 13f);
                float k = tints[plank] * grain;
                if (inPlank < size / 64) k *= 0.45f;   // the seam (2 px of 128)
                px[y * size + x] = new Color(baseCol.r * k, baseCol.g * k, baseCol.b * k, 1f);
            }
            tex.SetPixels(px);
            tex.Apply(true);
            Sharpen(tex);
            return tex;
        }

        // Crisp from a distance and at a glancing angle (the deck and walkways are seen almost
        // edge-on): trilinear with full anisotropic filtering, and a slight lean to the sharper mips.
        private static void Sharpen(Texture2D tex)
        {
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 16;
            tex.mipMapBias = -0.4f;
        }

        private static float TileNoise(int x, int y, int size, float scale, int seed)
        {
            // Blend four offset samples so the texture tiles seamlessly.
            float fx = (float)x / size, fy = (float)y / size;
            float s = scale;
            float a = Mathf.PerlinNoise(seed + fx * s, seed + fy * s);
            float b = Mathf.PerlinNoise(seed + (fx - 1f) * s, seed + fy * s);
            float c = Mathf.PerlinNoise(seed + fx * s, seed + (fy - 1f) * s);
            float d = Mathf.PerlinNoise(seed + (fx - 1f) * s, seed + (fy - 1f) * s);
            float ab = Mathf.Lerp(a, b, fx);
            float cd = Mathf.Lerp(c, d, fx);
            return Mathf.Lerp(ab, cd, fy);
        }

        // ---------------------------------------------------------------- primitives

        public static GameObject Group(Transform parent, string name, Vector3 localPos, float yaw = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return go;
        }

        public static GameObject Prim(PrimitiveType type, Transform parent, string name, Vector3 localPos,
                                      Vector3 scale, Material mat, bool collider = true, Vector3? euler = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(euler ?? Vector3.zero);
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            if (!collider) Object.Destroy(go.GetComponent<Collider>());
            return go;
        }

        public static GameObject Box(Transform p, string n, Vector3 pos, Vector3 size, Material m, bool col = true, Vector3? euler = null)
            => Prim(PrimitiveType.Cube, p, n, pos, size, m, col, euler);

        // Cylinder with its base at pos (Unity's primitive is centred and 2 units tall).
        // Wider-than-tall cylinders (decks, slabs, planters) get a mesh collider - Unity's
        // default capsule collider would balloon into a huge sphere on a flat disc.
        public static GameObject Post(Transform p, string n, Vector3 basePos, float radius, float height, Material m, bool col = true)
        {
            var go = Prim(PrimitiveType.Cylinder, p, n, basePos + Vector3.up * (height * 0.5f),
                          new Vector3(radius * 2f, height * 0.5f, radius * 2f), m, col);
            if (col && radius * 2f > height)
            {
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
            }
            return go;
        }

        public static GameObject Ball(Transform p, string n, Vector3 pos, Vector3 size, Material m, bool col = false, Vector3? euler = null)
            => Prim(PrimitiveType.Sphere, p, n, pos, size, m, col, euler);

        public static Light AddLight(Transform parent, Vector3 localPos, Color colour, float range, float intensity,
                                     LightType type = LightType.Point, bool shadows = false)
        {
            var go = new GameObject("Light");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var l = go.AddComponent<Light>();
            l.type = type;
            l.color = colour;
            l.range = range;
            l.intensity = intensity;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            return l;
        }

        // ---------------------------------------------------------------- meshes

        public static GameObject MeshObject(Transform parent, string name, Mesh mesh, Material[] mats, bool collider)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return go;
        }

        // Extrudes a rectangular cross-section (halfWidth x thickness, hanging below the path)
        // along a path of centre-top points. Used for the bridge deck, road surface and pier.
        public static Mesh Ribbon(IList<Vector3> path, float halfWidth, float thickness, float uvLength = 4f)
        {
            int n = path.Count;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            var right = new Vector3[n];
            var dist = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 fwd = (path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)]);
                fwd.y = 0f;
                right[i] = Vector3.Cross(Vector3.up, fwd.normalized);
                dist[i] = i == 0 ? 0f : dist[i - 1] + Vector3.Distance(path[i], path[i - 1]);
            }

            // Corners of the cross-section: top-left, top-right, bottom-right, bottom-left.
            Vector3 Corner(int i, int c)
            {
                Vector3 side = right[i] * (c == 0 || c == 3 ? -halfWidth : halfWidth);
                Vector3 down = c >= 2 ? Vector3.down * thickness : Vector3.zero;
                return path[i] + side + down;
            }

            // Four long faces: top (0->1), right (1->2), bottom (2->3), left (3->0).
            for (int face = 0; face < 4; face++)
            {
                int a = face, b = (face + 1) % 4;
                int start = verts.Count;
                for (int i = 0; i < n; i++)
                {
                    verts.Add(Corner(i, a));
                    verts.Add(Corner(i, b));
                    float v = dist[i] / uvLength;
                    uvs.Add(new Vector2(0f, v));
                    uvs.Add(new Vector2(1f, v));
                }
                for (int i = 0; i < n - 1; i++)
                {
                    int s = start + i * 2;
                    tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);
                    tris.Add(s + 1); tris.Add(s + 2); tris.Add(s + 3);
                }
            }

            // End caps.
            foreach (int i in new[] { 0, n - 1 })
            {
                int s = verts.Count;
                for (int c = 0; c < 4; c++) { verts.Add(Corner(i, c)); uvs.Add(new Vector2(c == 1 || c == 2 ? 1 : 0, c >= 2 ? 1 : 0)); }
                if (i == 0) { tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3); }
                else { tris.Add(s); tris.Add(s + 2); tris.Add(s + 1); tris.Add(s); tris.Add(s + 3); tris.Add(s + 2); }
            }

            var mesh = new Mesh { name = "Ribbon" };
            mesh.vertices = verts.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Merges every mesh under root into one mesh per material (colliders stay where they
        // are). Thousands of leaf/trunk primitives become a handful of draw calls.
        public static int CombineByMaterial(Transform root)
        {
            var groups = new Dictionary<System.IntPtr, (Material mat, List<CombineInstance> parts)>();
            var toStrip = new List<MeshRenderer>();
            var toLocal = root.worldToLocalMatrix;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = mr.GetComponent<MeshFilter>();
                var mat = mr.sharedMaterial;
                if (mf == null || mf.sharedMesh == null || mat == null) continue;
                if (!groups.TryGetValue(mat.Pointer, out var g))
                {
                    g = (mat, new List<CombineInstance>());
                    groups[mat.Pointer] = g;
                }
                g.parts.Add(new CombineInstance { mesh = mf.sharedMesh, subMeshIndex = 0, transform = toLocal * mr.transform.localToWorldMatrix });
                toStrip.Add(mr);
            }

            int made = 0;
            foreach (var g in groups.Values)
            {
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<CombineInstance>(g.parts.Count);
                for (int i = 0; i < g.parts.Count; i++) arr[i] = g.parts[i];
                var mesh = new Mesh { name = "Combined_" + g.mat.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.CombineMeshes(arr, true, true);
                MeshObject(root, "Combined", mesh, new[] { g.mat }, false);
                made++;
            }

            foreach (var mr in toStrip)
            {
                var mf = mr.GetComponent<MeshFilter>();
                Object.Destroy(mr);
                if (mf != null) Object.Destroy(mf);
            }
            return made;
        }

        // A double-sided flat triangle (for gable ends).
        public static Mesh Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var mesh = new Mesh { name = "Triangle" };
            mesh.vertices = new[] { a, b, c, a, b, c };
            mesh.uv = new[] { Vector2.zero, Vector2.right, new Vector2(0.5f, 1f), Vector2.zero, Vector2.right, new Vector2(0.5f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 3, 4, 5 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---------------------------------------------------------------- text

        // Text auto-sizes to fit a width x height box (metres). yaw = the direction it faces.
        public static void Text(Transform parent, string text, Vector3 localPos, float yaw, float width, float height, Color colour, string name = "Text")
        {
            try
            {
                if (!_fontSearched)
                {
                    _fontSearched = true;
                    foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    {
                        _font = f;
                        if (f.name.Contains("Bold") || f.name.Contains("Sans")) break;
                    }
                }

                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = localPos;
                // TMP reads correctly when looked at along its +Z, so turn it to face -yaw.
                go.transform.localRotation = Quaternion.Euler(0f, yaw + 180f, 0f);
                var tmp = go.AddComponent<TextMeshPro>();
                if (_font != null) tmp.font = _font;
                tmp.text = text;
                tmp.color = colour;
                Layout(tmp, width, height);
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't create sign text: " + e.Message);
            }
        }

        // One line, auto-sized to fit a width x height box (metres). Call again after the game
        // rewrites a text on an object that was built inactive - TMP resets its box on wake-up.
        private static readonly Dictionary<System.IntPtr, Vector2> _textBoxes = new Dictionary<System.IntPtr, Vector2>();

        public static void Layout(TextMeshPro tmp, float width, float height)
        {
            if (tmp == null) return;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 0.1f;
            tmp.fontSizeMax = 60f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.rectTransform.sizeDelta = new Vector2(width, height);
            tmp.ForceMeshUpdate(false, false);
            _textBoxes[tmp.Pointer] = new Vector2(width, height);
        }

        // Re-applies the layout of every sign text under root (see Layout).
        public static void RelayoutAll(Transform root)
        {
            if (root == null) return;
            foreach (var tmp in root.GetComponentsInChildren<TextMeshPro>(true))
                if (_textBoxes.TryGetValue(tmp.Pointer, out var box)) Layout(tmp, box.x, box.y);
        }
    }
}

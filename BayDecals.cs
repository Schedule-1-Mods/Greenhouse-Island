using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PelicanKey
{
    // Loading-bay numbers the game doesn't have. A bay's markings are one decal whose material
    // has the number painted into its texture ("Loading dock decal mat 1", "... 2", "... 3" -
    // no property has a 4th bay). For another number: the markings without any number (what the
    // game's "1", "2" and "3" textures have in common - per pixel, the least painted of the
    // three), plus the number painted flat on the ground where the game's numbers go, in the
    // same colour.
    internal static class BayDecals
    {
        private static Material _blank;
        private static Rect _digitUv;
        private static Color _digitColour = new Color(0.95f, 0.62f, 0.12f);
        private static bool _tried;

        // Gives the decal the given number. Returns false if it can't be done.
        public static bool Number(DecalProjector decal, int number)
        {
            if (decal == null) return false;
            if (!_tried) { _tried = true; MakeBlank(); }
            if (_blank == null) return false;
            decal.material = _blank;

            // Where the number sits on the ground: the digit's spot in the texture, through the
            // projector (texture u,v -> the projector's local x,y; it projects along its z).
            var uvCentre = _digitUv.center;
            var puv = new Vector2((uvCentre.x - decal.uvBias.x) / Mathf.Max(decal.uvScale.x, 0.0001f),
                                  (uvCentre.y - decal.uvBias.y) / Mathf.Max(decal.uvScale.y, 0.0001f));
            var size = decal.size;
            var local = new Vector3((puv.x - 0.5f) * size.x, (puv.y - 0.5f) * size.y, 0f) + decal.pivot;
            var world = decal.transform.TransformPoint(local);
            float w = _digitUv.width / Mathf.Max(decal.uvScale.x, 0.0001f) * size.x;
            float h = _digitUv.height / Mathf.Max(decal.uvScale.y, 0.0001f) * size.y;

            // Flat on the ground, reading the same way as the game's numbers.
            var up = decal.transform.up; up.y = 0f;
            if (up.sqrMagnitude < 0.001f) up = decal.transform.forward;
            var holder = new GameObject($"BayNumber{number}");
            holder.transform.SetParent(decal.transform, true);
            var ground = Siting.GroundHeight(world.x, world.z, null);
            holder.transform.position = new Vector3(world.x, (ground ?? world.y) + 0.02f, world.z);
            holder.transform.rotation = Quaternion.LookRotation(Vector3.down, up.normalized);
            Kit.Text(holder.transform, number.ToString(), Vector3.zero, 0f, Mathf.Max(w, h * 0.7f) * 1.2f, h * 1.15f, _digitColour, "Number");
            var text = holder.transform.Find("Number");
            if (text != null) text.localRotation = Quaternion.identity;
            return true;
        }

        // The game's bay marking materials (numbers 1-3) and which of their textures is the paint.
        private static Material[] _mats;
        private static string _prop;
        private static bool FindMarkings()
        {
            if (_mats != null) return _prop != null;
            _mats = new Material[3];
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                for (int i = 0; i < 3; i++)
                    if (m.name == $"Loading dock decal mat {i + 1}") _mats[i] = m;
            }
            if (_mats[0] == null || _mats[1] == null)
            {
                Core.Log.Warning("Couldn't find the game's loading bay markings.");
                return false;
            }
            // The texture that differs between them is the paint (it has the number in it).
            foreach (var name in _mats[0].GetTexturePropertyNames())
            {
                var a = _mats[0].GetTexture(name); var b = _mats[1].GetTexture(name);
                if (a != null && b != null && a.Pointer != b.Pointer) { _prop = name; break; }
            }
            if (_prop == null) Core.Log.Warning("The loading bay markings' paint isn't in a texture.");
            return _prop != null;
        }

        // Repaints a bay's markings in black spray paint (same lines, lettering and number).
        private static readonly Dictionary<string, Material> _black = new Dictionary<string, Material>();
        public static bool Blacken(DecalProjector decal)
        {
            try
            {
                var m = decal != null ? decal.material : null;
                if (m == null || !FindMarkings()) return false;
                if (!_black.TryGetValue(m.name, out var black) || black == null)
                {
                    var src = m.GetTexture(_prop);
                    if (src == null) return false;
                    var px = Readable(src, src.width, src.height).GetPixels();
                    for (int i = 0; i < px.Length; i++)
                    {
                        // Near-black, a touch of the original's shading so it still looks sprayed on.
                        float lum = (px[i].r + px[i].g + px[i].b) / 3f;
                        float v = 0.03f + lum * 0.06f;
                        px[i] = new Color(v, v, v, px[i].a);
                    }
                    var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true) { name = m.name + " (black)", wrapMode = src.wrapMode, filterMode = src.filterMode };
                    tex.SetPixels(px);
                    tex.Apply(true);
                    black = new Material(m) { name = m.name + " (black)" };
                    black.SetTexture(_prop, tex);
                    _black[m.name] = black;
                }
                decal.material = black;
                return true;
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't repaint the loading bay black: " + e.Message);
                return false;
            }
        }

        private static void MakeBlank()
        {
            try
            {
                if (!FindMarkings()) return;
                var mats = _mats;
                string prop = _prop;

                var src = mats[0].GetTexture(prop);
                int w = src.width, h = src.height;
                var pix = new List<Color[]>();
                foreach (var m in mats)
                    if (m != null) pix.Add(Readable(m.GetTexture(prop), w, h).GetPixels());

                // Least painted of the three at every pixel = the markings with no number.
                var blank = new Color[w * h];
                float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f, r = 0f, g = 0f, b2 = 0f; int n = 0;
                for (int i = 0; i < blank.Length; i++)
                {
                    var best = pix[0][i];
                    foreach (var p in pix) if (p[i].a < best.a) best = p[i];
                    blank[i] = best;
                    // Where the "1" was (painted in mat 1, not in the blank): the digit's spot and colour.
                    if (pix[0][i].a - best.a > 0.4f)
                    {
                        float x = (i % w + 0.5f) / w, y = (i / w + 0.5f) / h;
                        minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                        r += pix[0][i].r; g += pix[0][i].g; b2 += pix[0][i].b; n++;
                    }
                }
                if (n == 0) { Core.Log.Warning("Couldn't find the number in the loading bay markings."); return; }
                _digitColour = new Color(r / n, g / n, b2 / n, 1f);
                // The "1" is narrow: give the area a digit's usual width, centred on it.
                float digitH = maxY - minY, digitW = Mathf.Max(maxX - minX, digitH * 0.65f);
                float cx = (minX + maxX) * 0.5f;
                _digitUv = new Rect(cx - digitW * 0.5f, minY, digitW, digitH);

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Loading dock decal (no number)", wrapMode = src.wrapMode, filterMode = src.filterMode };
                tex.SetPixels(blank);
                tex.Apply(true);
                _blank = new Material(mats[0]) { name = "Loading dock decal mat (no number)" };
                _blank.SetTexture(prop, tex);
                Core.Log.Msg($"Made unnumbered loading bay markings ({w}x{h}); numbers go at {_digitUv}.");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't make new loading bay numbers: " + e.Message);
                _blank = null;
            }
        }

        // A CPU copy of any texture (the game's aren't readable): drawn into a render texture and
        // read back.
        private static Texture2D Readable(Texture src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return t;
        }
    }
}

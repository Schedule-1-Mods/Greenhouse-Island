using System.Collections.Generic;
using System.Linq;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.Property;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Copies of the game's own props (the dumpster other properties have, the "for sale" poster at
    // Ray's), so the island matches the rest of the game instead of using stand-in shapes.
    internal static class GameProps
    {
        // Copies a scene object's visuals under parent. Networked components are stripped first
        // (the copy is local scenery; a stray NetworkObject would confuse the game's networking).
        public static GameObject CloneVisual(GameObject source, Transform parent)
        {
            var holder = new GameObject("CloneHolder");
            holder.SetActive(false);
            var copy = Object.Instantiate(source, holder.transform);
            foreach (var nb in copy.GetComponentsInChildren<NetworkBehaviour>(true)) Object.DestroyImmediate(nb);
            foreach (var no in copy.GetComponentsInChildren<NetworkObject>(true)) Object.DestroyImmediate(no);
            copy.transform.SetParent(parent, false);
            copy.SetActive(true);
            Object.Destroy(holder);
            return copy;
        }

        // Copies a piece of the game's level exactly as it looks, even when it's static-batched
        // (most of the level is: its meshes were merged into big combined meshes at build time, so
        // a plain copy drags a whole area's scenery along - see BatchedMesh). Each renderer's own
        // slice is cut back out, at the game's exact size, with a matching collider. Returns a root
        // posed where the source is in the world (unit scale), ready to be re-parented and moved.
        public static GameObject CloneLevelPiece(GameObject source, string name, out int parts) => CloneLevelPiece(source, name, false, out parts);

        // skipText: leave out TextMeshPro text (its mesh is generated from the text, not level
        // geometry) - the caller copies those as live text instead.
        public static GameObject CloneLevelPiece(GameObject source, string name, bool skipText, out int parts)
        {
            parts = 0;
            var root = new GameObject(name);
            root.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);

            // Only the most detailed version of each part (skip lower LODs).
            var skip = new HashSet<System.IntPtr>();
            foreach (var lg in source.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                for (int i = 1; i < lods.Length; i++)
                    foreach (var r in lods[i].renderers) if (r != null) skip.Add(r.Pointer);
            }

            foreach (var r in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || skip.Contains(r.Pointer)) continue;
                if (skipText && r.GetComponent<Il2CppTMPro.TMP_Text>() != null) continue;
                if (AddLevelPart(root.transform, r, source.name)) parts++;
            }
            return root;
        }

        // Copies one renderer of the level under root, exactly where it is in the world now.
        private static bool AddLevelPart(Transform root, MeshRenderer r, string what)
        {
            var mesh = BatchedMesh.Extract(r, out var how);
            if (mesh == null) { Core.Log.Warning($"Couldn't copy part '{r.name}' of '{what}': {how}"); return false; }
            var part = new GameObject(r.name);
            part.transform.SetParent(root, false);
            // The extracted mesh is in world orientation, centred on the part.
            part.transform.position = r.bounds.center;
            part.transform.rotation = Quaternion.identity;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterials = r.sharedMaterials;
            mr.shadowCastingMode = r.shadowCastingMode;
            part.AddComponent<MeshCollider>().sharedMesh = mesh;
            part.layer = r.gameObject.layer;
            return true;
        }

        // ------------------------------------------------------------ looking at a model

        // How "front-like" a model looks from one side: it's photographed from 0.5 m out along
        // `dir` (the caller parks it alone up in the empty sky first), with its own light, and
        // scored on brightness plus detail. A face with detail scores well above a plain back.
        public static float LookScore(GameObject model, Vector3 center, Vector3 dir)
        {
            const int size = 48;
            var camGo = new GameObject("LookCamera");
            var rt = new RenderTexture(size, size, 16);
            try
            {
                camGo.transform.position = center + dir.normalized * 0.5f;
                camGo.transform.LookAt(center);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.cullingMask = ~0;
                cam.fieldOfView = 35f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 3f;
                cam.targetTexture = rt;
                var light = camGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 3f;
                light.intensity = 3f;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                double sum = 0, sumSq = 0;
                int n = 0;
                foreach (var c in tex.GetPixels())
                {
                    float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                    if (lum < 0.002f) continue;   // background
                    sum += lum; sumSq += lum * lum; n++;
                }
                Object.Destroy(tex);
                if (n == 0) return 0f;
                double mean = sum / n;
                double sd = System.Math.Sqrt(System.Math.Max(0, sumSq / n - mean * mean));
                return (float)(mean + sd * 2.0);
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't look at the model: " + e.Message);
                return 0f;
            }
            finally
            {
                Object.Destroy(camGo);
                rt.Release();
            }
        }

        // ------------------------------------------------------------ the game's weed plant

        // A copy of the game's own fully grown weed plant (the last growth-stage model, with every
        // bud site showing its bud), scaled up so it stands `height` metres tall on `parent`'s
        // origin. Harvesting/clicking/physics bits are stripped - it's scenery. Returns null if
        // the game's plant can't be found. Also writes the plant's parts to UserData/PelicanKey.
        public static GameObject CloneGrownWeedPlant(Transform parent, float height)
        {
            Il2CppScheduleOne.Growing.WeedPlant template = null;
            foreach (var wp in Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.Growing.WeedPlant>())
            {
                if (wp == null || wp.GrowthStages == null || wp.GrowthStages.Length == 0) continue;
                template = wp;
                string wn = wp.name.ToLowerInvariant();
                if (wn.Contains("purple") || wn.Contains("granddaddy") || wn.Contains("gdp")) break;   // the purple strain
            }
            if (template == null) return null;

            var stage = template.FinalGrowthStage;
            DumpHierarchy(template.transform, "weed plant '" + template.name + "'");

            var holder = new GameObject("WeedPlantHolder");
            holder.SetActive(false);
            var copy = Object.Instantiate(stage.gameObject, holder.transform);
            copy.name = "GameWeedPlant";

            // Every bud site on, and any empty site given the game's bud branch.
            var copyStage = copy.GetComponent<Il2CppScheduleOne.Growing.PlantGrowthStage>();
            if (copyStage != null && copyStage.GrowthSites != null)
            {
                foreach (var site in copyStage.GrowthSites)
                {
                    if (site == null) continue;
                    site.gameObject.SetActive(true);
                    if (site.GetComponentInChildren<Renderer>(true) == null && template.BranchPrefab != null)
                    {
                        var bud = Object.Instantiate(template.BranchPrefab.gameObject, site);
                        bud.transform.localPosition = Vector3.zero;
                        bud.transform.localRotation = Quaternion.identity;
                    }
                }
            }
            foreach (var tr in copy.GetComponentsInChildren<Transform>(true)) tr.gameObject.SetActive(true);

            // Scenery only: no harvesting, clicking, networking or physics.
            foreach (var h in copy.GetComponentsInChildren<Il2CppScheduleOne.Growing.PlantHarvestable>(true)) Object.DestroyImmediate(h);
            foreach (var nb in copy.GetComponentsInChildren<NetworkBehaviour>(true)) Object.DestroyImmediate(nb);
            foreach (var no in copy.GetComponentsInChildren<NetworkObject>(true)) Object.DestroyImmediate(no);
            foreach (var c in copy.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var rb in copy.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
            int budSites = copyStage != null && copyStage.GrowthSites != null ? copyStage.GrowthSites.Length : 0;
            // Nor any of the game's scripts (some recolour the buds by themselves).
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
            foreach (var tr in copy.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = parent.gameObject.layer;

            copy.transform.SetParent(parent, false);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = Vector3.one;
            copy.SetActive(true);
            Object.Destroy(holder);

            // Scale to the wanted height and stand it on the origin.
            Bounds b = default; bool any = false;
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            if (!any || b.size.y < 0.01f)
            {
                Object.Destroy(copy);
                return null;
            }
            float k = height / b.size.y;
            copy.transform.localScale = Vector3.one * k;
            b = default; any = false;
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            var baseWorld = parent.position;
            copy.transform.position += new Vector3(baseWorld.x - b.center.x, baseWorld.y - b.min.y, baseWorld.z - b.center.z);

            Paint(copy);
            Core.Log.Msg($"Weed plant copied from the game's '{template.name}' (final growth stage '{stage.name}', {budSites} bud sites), scaled x{k:0.0}, painted pink.");
            return copy;
        }

        // In the chosen plant and bud colours (pink unless changed - see TreeColours): the game's
        // materials are colour textures on URP Lit, so the texture goes and a flat colour goes in
        // (the models are low-poly, flat-shaded). One recolourable copy per game material.
        private static readonly System.Collections.Generic.Dictionary<string, Material> _painted = new System.Collections.Generic.Dictionary<string, Material>();
        public static void ClearPainted() => _painted.Clear();
        private static void Paint(GameObject plant)
        {
            foreach (var r in plant.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src == null) continue;
                    if (!_painted.TryGetValue(src.name, out var m) || m == null)
                        _painted[src.name] = m = TreeColours.Recolourable(src);
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
                // The game sets colours per renderer too (property blocks), which win over the
                // material's colour - drop them so the chosen colours show.
                r.SetPropertyBlock(null);
            }
        }

        // Writes a model's parts (active state, mesh, materials, size) to UserData/PelicanKey.
        private static void DumpHierarchy(Transform root, string what)
        {
            try
            {
                var lines = new List<string> { what };
                void Walk(Transform t, string indent)
                {
                    string info = t.name + (t.gameObject.activeSelf ? "" : " (off)");
                    var mf = t.GetComponent<MeshFilter>();
                    var r = t.GetComponent<Renderer>();
                    if (mf != null && mf.sharedMesh != null) info += $"  mesh={mf.sharedMesh.name} verts={mf.sharedMesh.vertexCount}";
                    if (r != null)
                    {
                        var mats = new List<string>();
                        foreach (var m in r.sharedMaterials) mats.Add(m != null ? m.name + "/" + (m.shader != null ? m.shader.name : "?") : "null");
                        info += $"  mats=[{string.Join(", ", mats)}]  size={r.bounds.size}";
                    }
                    lines.Add(indent + info);
                    for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), indent + "  ");
                }
                Walk(root, "");
                string dir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PelicanKey");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "weedplant_model.txt"), lines);
            }
            catch { }
        }

        // ------------------------------------------------------------ dumpster

        // The dumpster model another property uses for its trash, plus where that property's
        // cleaners stand and drop bags, relative to the model.
        public static bool FindDumpster(Property except, out GameObject model, out Vector3 standLocal, out Vector3 dropLocal)
        {
            model = null; standLocal = dropLocal = Vector3.zero;

            // Every dumpster-sized object in the scene named like a dumpster.
            var candidates = new List<GameObject>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || !go.scene.IsValid() || !go.activeInHierarchy) continue;
                string n = go.name.ToLowerInvariant();
                if (!n.Contains("dumpster") && !n.Contains("skip bin") && !n.Contains("trash bin")) continue;
                float size = Size(go.transform);
                if (size < 1.5f || size > 7f) continue;
                // Take the top-most dumpster-named object, not each of its parts.
                var parent = go.transform.parent;
                if (parent != null && parent.name.ToLowerInvariant().Contains("dumpster")) continue;
                candidates.Add(go);
            }

            // Prefer one that belongs to a property's disposal area (so we also know where the
            // cleaners stand and drop the bags).
            foreach (var p in Property.Properties)
            {
                try
                {
                    if (p == null || p == except || p.DisposalArea == null || p.DisposalArea.TrashDropPoint == null) continue;
                    var drop = p.DisposalArea.TrashDropPoint.position;
                    foreach (var c in candidates)
                    {
                        if (Vector3.Distance(c.transform.position, drop) > 5f) continue;
                        model = c;
                        standLocal = Unscaled(c.transform, p.DisposalArea.StandPoint.position);
                        dropLocal = Unscaled(c.transform, drop);
                        return true;
                    }
                }
                catch { }
            }
            if (candidates.Count > 0)
            {
                // No property uses one: stand in front of its long side, drop in over the top.
                model = candidates[0];
                Bounds b = default; bool any = false;
                foreach (var r in model.GetComponentsInChildren<Renderer>(true)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                var t = model.transform;
                var worldSide = b.size.x >= b.size.z ? new Vector3(0f, 0f, b.extents.z + 0.8f) : new Vector3(b.extents.x + 0.8f, 0f, 0f);
                standLocal = Unscaled(t, new Vector3(b.center.x, b.min.y, b.center.z) + worldSide);
                dropLocal = Unscaled(t, new Vector3(b.center.x, b.max.y + 0.2f, b.center.z));
                return true;
            }
            return false;
        }

        // A world point relative to t's position and rotation, in metres (ignoring t's scale -
        // the copy made by CloneLevelPiece has unit scale).
        private static Vector3 Unscaled(Transform t, Vector3 world) => Quaternion.Inverse(t.rotation) * (world - t.position);

        private static float Size(Transform t)
        {
            Bounds b = default;
            bool any = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b.size.magnitude : 0f;
        }

        // ------------------------------------------------------------ wall-mounted things

        // Which way a wall-mounted object faces: probe along its own axes for the wall it's
        // fixed to; the front is the opposite side. Returns a local-space axis.
        public static Vector3 FrontAxis(Transform t)
        {
            var axes = new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
            float bestDist = float.MaxValue;
            Vector3 back = Vector3.back;
            foreach (var axis in axes)
            {
                var dir = t.TransformDirection(axis);
                foreach (var hit in Physics.RaycastAll(t.position, dir, 0.8f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.transform.IsChildOf(t)) continue;
                    if (hit.distance < bestDist) { bestDist = hit.distance; back = axis; }
                }
            }
            return -back;
        }

        // ------------------------------------------------------------ listing poster at Ray's

        // A copy of another property's "for sale" poster, placed on the board at Ray's.
        // Prefers the spot of a property you already own (its own poster is hidden for good).
        public static Transform MakeListingPoster(Property except)
        {
            Transform template = null, freeSpot = null;
            var all = new List<Transform>();
            foreach (var p in Property.Properties)
            {
                // Properties only - businesses have their own board.
                if (p == null || p == except || p.ListingPoster == null || p.TryCast<Il2CppScheduleOne.Property.Business>() != null) continue;
                all.Add(p.ListingPoster);
                template ??= p.ListingPoster;
                if (p.IsOwned && freeSpot == null) freeSpot = p.ListingPoster;
            }
            if (template == null) return null;

            var copy = Object.Instantiate(template.gameObject, template.parent);
            copy.name = "ListingPoster_JungleGreenhouseIsland";
            var t = copy.transform;
            if (freeSpot != null)
            {
                t.position = freeSpot.position;
                t.rotation = freeSpot.rotation;
            }
            else
            {
                // Below the lowest poster on the board.
                Transform lowest = template;
                foreach (var a in all) if (a.localPosition.y < lowest.localPosition.y) lowest = a;
                float h = Size(lowest) * 0.7f;
                t.localPosition = lowest.localPosition + Vector3.down * Mathf.Max(0.4f, h);
                t.localRotation = lowest.localRotation;
            }
            copy.SetActive(true);

            var names = new List<string>();
            foreach (var tr in copy.GetComponentsInChildren<Transform>(true)) names.Add(tr.name);
            Core.Log.Msg("Listing poster parts: " + string.Join(", ", names));
            return t;
        }

        // Moves our poster onto the "Properties for sale" board, right of the Docks Warehouse
        // poster, and writes its details the way the game does for its own posters. Called once
        // the save has loaded, when ownership is known.
        private static Transform _refPoster;   // the Docks Warehouse poster, for sizes

        public static void PlaceListingPoster(Transform poster, Property prop)
        {
            if (poster == null || prop == null) return;
            var board = poster.parent;
            var shown = new List<Transform>();
            var hidden = new List<Transform>();
            foreach (var p in Property.Properties)
            {
                if (p == null || p == prop || p.ListingPoster == null || p.ListingPoster.parent != board) continue;
                if (p.TryCast<Il2CppScheduleOne.Property.Business>() != null) continue;
                (p.ListingPoster.gameObject.activeSelf ? shown : hidden).Add(p.ListingPoster);
            }
            // Right of the Docks Warehouse poster, the same distance on as that is from the poster
            // before it in its row, and the same size. (The hidden posters of owned properties
            // overlap the shown ones, so their spots aren't free space.)
            Transform docks = null;
            foreach (var p in Property.Properties)
                if (p != null && p != prop && p.ListingPoster != null && p.ListingPoster.parent == board
                    && p.PropertyCode != null && p.PropertyCode.ToLowerInvariant().Contains("dock"))
                    docks = p.ListingPoster;
            if (docks != null)
            {
                // Reading direction on the board: along the poster's title text.
                var title = docks.Find("Title");
                var right = (title != null ? title.right : docks.right).normalized;
                float docksCol = Vector3.Dot(docks.position, right), step = 0f;
                foreach (var o in shown.Concat(hidden))
                {
                    if (o == docks || Mathf.Abs(o.position.y - docks.position.y) > 0.1f) continue;
                    float gap = docksCol - Vector3.Dot(o.position, right);
                    if (gap > 0.05f && (step == 0f || gap < step)) step = gap;
                }
                if (step == 0f) step = Size(docks) * 1.1f;
                poster.SetPositionAndRotation(docks.position + right * step, docks.rotation);
                poster.localScale = docks.localScale;

                // Same layout inside as the Docks poster too (our copy may be of a poster laid
                // out differently): each part where Docks has it, same size.
                _refPoster = docks;
                foreach (var n in new[] { "Paper", "Image", "Title", "Price", "Parking", "Employee" })
                {
                    var a = poster.Find(n); var b = docks.Find(n);
                    if (a == null || b == null) continue;
                    a.localPosition = b.localPosition; a.localRotation = b.localRotation; a.localScale = b.localScale;
                }
            }
            if (!prop.IsOwned) poster.gameObject.SetActive(true);

            // Its details, as the game writes them on its own posters.
            SetPosterText(poster, "Title", prop.propertyName);
            SetPosterText(poster, "Price", "$" + Mathf.RoundToInt(prop.Price).ToString("N0"));
            SetPosterText(poster, "Parking/Text", (prop.LoadingDocks != null ? prop.LoadingDocks.Length : 0).ToString());
            SetPosterText(poster, "Employee/Text", prop.EmployeeCapacity.ToString());
        }

        private static void SetPosterText(Transform poster, string path, string text)
        {
            var t = poster.Find(path);
            var tmp = t != null ? t.GetComponent<Il2CppTMPro.TMP_Text>() : null;
            if (tmp != null) tmp.text = text;
        }

        // Puts a picture onto the poster: the textured, non-text surface that isn't the poster's
        // own paper (i.e. the smaller of the textured surfaces), or one named like a picture.
        public static bool SetPosterPicture(Transform poster, Texture2D picture)
        {
            if (poster == null || picture == null) return false;
            Renderer target = null;
            float bestArea = float.MaxValue;
            foreach (var r in poster.GetComponentsInChildren<Renderer>(true))
            {
                if (r.GetComponent<Il2CppTMPro.TMP_Text>() != null) continue;
                string n = r.gameObject.name.ToLowerInvariant();
                bool named = n.Contains("image") || n.Contains("photo") || n.Contains("picture") || n.Contains("render") || n.Contains("preview");
                var m = r.sharedMaterial;
                if (!named && (m == null || m.mainTexture == null)) continue;
                var s = r.bounds.size;
                float area = Mathf.Max(s.x * s.y, Mathf.Max(s.x * s.z, s.y * s.z));
                if (named) area = -1f;
                if (area < bestArea) { bestArea = area; target = r; }
            }

            var sr = target != null ? target.TryCast<SpriteRenderer>() : null;
            if (sr != null)
            {
                // As big as the reference poster's photo (Docks Warehouse): same shape (the
                // middle of our photo cropped to it) and same size on the poster.
                var refT = _refPoster != null ? _refPoster.Find(target.gameObject.name) : null;
                var refSr = refT != null ? refT.GetComponent<SpriteRenderer>() : null;
                if (refSr != null && refSr.sprite != null)
                {
                    var size = refSr.sprite.bounds.size;
                    float aspect = size.x / Mathf.Max(size.y, 0.0001f);
                    float w = picture.width, h = picture.height;
                    if (w / h > aspect) w = h * aspect; else h = w / aspect;
                    var crop = new Rect((picture.width - w) * 0.5f, (picture.height - h) * 0.5f, w, h);
                    var pivot = new Vector2(refSr.sprite.pivot.x / refSr.sprite.rect.width, refSr.sprite.pivot.y / refSr.sprite.rect.height);
                    sr.sprite = Sprite.Create(picture, crop, pivot, w / Mathf.Max(size.x, 0.0001f));
                    sr.drawMode = refSr.drawMode;
                    if (refSr.drawMode != SpriteDrawMode.Simple) sr.size = refSr.size;
                    target.transform.localPosition = refT.localPosition;
                    target.transform.localRotation = refT.localRotation;
                    target.transform.localScale = refT.localScale;
                }
                else
                    sr.sprite = Sprite.Create(picture, new Rect(0, 0, picture.width, picture.height), new Vector2(0.5f, 0.5f), 100f);
                return true;
            }
            if (target != null)
            {
                var mat = new Material(target.sharedMaterial);
                mat.mainTexture = picture;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", picture);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                target.sharedMaterial = mat;
                return true;
            }

            var img = poster.GetComponentInChildren<UnityEngine.UI.Image>(true);
            if (img != null)
            {
                img.sprite = Sprite.Create(picture, new Rect(0, 0, picture.width, picture.height), new Vector2(0.5f, 0.5f), 100f);
                return true;
            }
            return false;
        }

        // A photo of the dome from a camera placed just off the platform.
        public static Texture2D PhotographDome(Site site)
        {
            const int W = 512, H = 384;
            var camGo = new GameObject("ListingCamera");
            var rt = new RenderTexture(W, H, 24);
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 600f;
                cam.targetTexture = rt;
                camGo.transform.position = site.Local(-26f, 11f, -30f);
                camGo.transform.LookAt(site.Local(0f, 4.5f, 0f));
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                return tex;
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't take the listing photo: " + e.Message);
                return null;
            }
            finally
            {
                Object.Destroy(camGo);
                rt.Release();
            }
        }
    }
}

using System.Collections.Generic;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Persistence;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Builds Greenhouse Island: a round floating timber platform just off the Docks with a
    // glass greenhouse dome in the middle (a big weed plant at its centre, its floor a build
    // grid - see GreenhouseGrid), a deck ring around the dome for employees, water tanks and a
    // wind turbine, and a boat pier back at the Docks.
    //
    // Everything hangs off one "Island" transform centred on the platform and facing away from
    // the mainland, so positions below are platform-local:
    //   x = right, z = away from the Docks (boats come in from -z), y = 0 on the deck
    //   (Site.DeckAboveSea above the water).
    internal static class IslandBuilder
    {
        public sealed class Built
        {
            public Site Site;
            public bool InZone(Vector3 p) => Site.InZone(p);
        }

        public static Built Current;

        // Floating platform: radius and how far below the deck the sea is.
        public const float PlatformRadius = 18f;
        private const float SeaY = -Site.DeckAboveSea;

        // Glass dome: radius, height of the timber base wall, floor level, and the planter round
        // the weed plant in the middle.
        public const float DomeRadius = 12f, DomeWall = 1.2f, GhFloor = 0.15f, TreePlanterRadius = 2.2f;
        public const float GreenhouseDepth = DomeRadius * 2f;
        public static readonly Vector3 BuildHalfSize = new Vector3(11.5f, 8f, 11.5f);
        // The build grid covers the whole dome floor, all the way round, except the tree planter.
        public const float BuildInnerRadius = TreePlanterRadius + 0.6f, BuildOuterRadius = DomeRadius - 0.7f;

        // Where employees wait on the deck ring, as angles (0 = the far side, away from the Docks).
        public const float IdleArcFrom = -80f, IdleArcTo = 80f;
        public const float IdleRadius = DomeRadius + 1.4f, IdleRowGap = 1.3f;

        private static Transform _turbine;

        // Dome door: two sliding panels (local closed/open positions), how open it is, and the
        // save point that only works once the property is owned.
        // A doorway in the dome with its pair of sliding glass doors (one faces each walkway).
        private sealed class Door
        {
            public string Name;
            public readonly List<Transform> Panels = new List<Transform>();
            public readonly List<Vector3> Closed = new List<Vector3>();
            public readonly List<Vector3> Open = new List<Vector3>();
            public Transform Anchor;
            public Vector3 OutDir = Vector3.back;
            public float T, Want;
        }
        private static readonly List<Door> _doors = new List<Door>();
        private static float _nextLockedMsg;
        private static bool _doorLogged;
        private static GameObject _savePoint;
        // Map marker for finding the place before it's bought; the property's own marker takes
        // over once it's owned.
        private static GameObject _marker;

        private static GameObject _root;
        private static Transform _island;
        public static Transform Island => _island;
        // Where the water tap stands: a point on the inside face of the dome wall left of the
        // main door (world), and which way that face points (into the dome).
        public static Vector3 TapWallPoint, TapWallNormal;
        public static bool TapWallSet;
        private static Site _site;
        private static float R;

        // The build grid lives on its own object so moving the island doesn't delete it.
        private static GameObject _gridSpace;

        private static readonly List<Light> _nightLights = new List<Light>();
        private static bool? _night;

        private static Material mSand, mJungleFloor, mDirt, mConcrete, mGravel, mAsphalt, mLine, mWhiteLine, mDarkWood,
            mPlanks, mMetal, mFrame, mGlass, mWhite, mRed, mDark, mStone, mTrunk, mBark, mLeafDark, mLeafMid, mLeafLight,
            mFrond, mFrond2, mBanana, mMoss, mLampGlow, mSignGreen, mBlue, mTeal, mFlowerRed, mFlowerPink, mFlowerYellow,
            mFlowerOrange, mVine, mTank, mSignPink;

        public static Vector3 IslandLandingPoint;

        // ================================================================ lifecycle

        // full = the scene is changing, so the build grid goes too.
        public static void Clear(bool full)
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _island = null;
            Current = null;
            _nightLights.Clear();
            _turbine = null;
            _doors.Clear();
            _savePoint = null;
            TreeColours.Clear(); WindmillLights.Clear(); GameProps.ClearPainted(); RainShelter.Clear();
            ParkingBays.Clear();
            foreach (var bin in _removedBins) if (bin != null) bin.SetActive(true);   // put the game's bins back
            _removedBins.Clear();
            _night = null;

            if (full)
            {
                if (_gridSpace != null) Object.Destroy(_gridSpace);
                _gridSpace = null;
                GreenhouseGrid.Clear();
                JungleProperty.Clear();
            }
        }

        public static Transform GridSpace => _gridSpace != null ? _gridSpace.transform : null;
        public static Transform RootTransform => _root != null ? _root.transform : null;

        public static void Build(Site site)
        {
            Clear(false);
            Kit.ResetCaches();
            _site = site;
            R = site.Radius;

            _root = new GameObject("PelicanKey");
            var island = new GameObject("Island");
            island.transform.SetParent(_root.transform, false);
            island.transform.position = site.Center;
            island.transform.rotation = Quaternion.LookRotation(site.Dir);
            _island = island.transform;

            MakeMaterials();

            // Open the way before our own colliders exist, so nothing of ours is mistaken for a wall.
            int walls = Siting.ClearInvisibleWalls(site, _root.transform);

            BuildOceanPatchIfNeeded();
            BuildPlatform();
            BuildDocks();
            BuildGreenhouse();
            AddMapMarker();

            // Place (or move) the build space onto the greenhouse floor.
            if (_gridSpace == null) _gridSpace = new GameObject("PelicanKeyGreenhouseSpace");
            _gridSpace.transform.position = site.Local(0f, GhFloor, 0f);
            _gridSpace.transform.rotation = _island.rotation;

            Current = new Built
            {
                Site = site,
            };

            // Make the new colliders live in the physics scene now, not at the next physics step.
            Physics.SyncTransforms();
            Core.Log.Msg($"Greenhouse Island (floating platform) built at {site.Center} with {_docks.Count} walkway(s) from the shore ({walls} invisible wall(s) opened).");
        }

        public static void Tick(float dt)
        {
            if (_root == null) return;
            if (_marker != null && _marker.activeSelf == JungleProperty.IsOwned) _marker.SetActive(!JungleProperty.IsOwned);
            if (_turbine != null) _turbine.Rotate(0f, 0f, 70f * dt, Space.Self);

            bool owned = JungleProperty.IsOwned;
            if (_savePoint != null && _savePoint.activeSelf != owned) _savePoint.SetActive(owned);
            TickDoor(dt, owned);
        }

        private static void TickDoor(float dt, bool owned)
        {
            foreach (var d in _doors) TickDoor(d, dt, owned);
        }

        // Locked until the property is yours; then it slides open for you or your employees.
        private static void TickDoor(Door dr, float dt, bool owned)
        {
            if (dr.Anchor == null || dr.Panels.Count == 0) return;
            var door = dr.Anchor.position;
            // Flat distance (height doesn't matter), from wherever the player really is: their
            // body or, failing that, the camera.
            float Flat(Vector3 p) { float dy = Mathf.Abs(p.y - door.y); p -= door; p.y = 0f; return dy > 4f ? 99f : p.magnitude; }
            float playerDist = 99f;
            try
            {
                var player = Il2CppScheduleOne.PlayerScripts.Player.Local;
                if (player != null) playerDist = Flat(player.transform.position);
            }
            catch { }
            var cam = Camera.main;
            if (cam != null) playerDist = Mathf.Min(playerDist, Flat(cam.transform.position));

            const float OpenRange = 5.5f;
            bool someoneNear = playerDist < OpenRange;
            if (someoneNear && !_doorLogged)
            {
                _doorLogged = true;
                Core.Log.Msg($"Door: player {playerDist:0.0} m away, property owned = {owned} -> {(owned ? "opening" : "locked")}.");
            }
            if (!someoneNear && owned)
            {
                try
                {
                    foreach (var emp in JungleProperty.Property.Employees)
                        // Only employees right at the door (not the ones waiting on the deck nearby).
                        if (emp != null && Flat(emp.transform.position) < 2.2f) { someoneNear = true; break; }
                }
                catch { }
            }

            if (!owned && playerDist < 3f && Time.time > _nextLockedMsg)
            {
                _nextLockedMsg = Time.time + 5f;
                Hud.Toast("Locked. Buy Greenhouse Island from Ray's Realty ($0) to get in.");
            }

            float want = owned && someoneNear ? 1f : 0f;
            if (want != dr.Want)
            {
                dr.Want = want;
                Core.Log.Msg($"{dr.Name} {(want > 0.5f ? "sliding open" : "closing")} (player {playerDist:0.0} m away, owned = {owned}).");
            }
            dr.T = Mathf.MoveTowards(dr.T, want, dt * 3f);
            float k = Mathf.SmoothStep(0f, 1f, dr.T);
            // Step out from the frame first, then slide sideways (so they don't cut through the posts).
            float kOut = Mathf.Clamp01(k / 0.3f), kSlide = Mathf.Clamp01((k - 0.3f) / 0.7f);
            for (int i = 0; i < dr.Panels.Count; i++)
            {
                if (dr.Panels[i] == null) continue;
                var d = dr.Open[i] - dr.Closed[i];
                var outPart = dr.OutDir * Vector3.Dot(d, dr.OutDir);
                dr.Panels[i].localPosition = dr.Closed[i] + outPart * kOut + (d - outPart) * kSlide;
            }
        }

        public static void SetNight(bool night)
        {
            WindmillLights.SetNight(night);
            if (_night == night) return;
            _night = night;
            foreach (var l in _nightLights)
                if (l != null) l.enabled = night;
        }

        // ================================================================ floating platform

        private static void BuildPlatform()
        {
            var g = Kit.Group(_island, "Platform", Vector3.zero).transform;
            float pr = PlatformRadius;

            // Timber body sitting in the water, plank deck on top.
            Kit.Post(g, "Body", new Vector3(0f, SeaY - 0.7f, 0f), pr, -SeaY + 0.7f - 0.04f, mDarkWood);
            var deck = Kit.Post(g, "Deck", new Vector3(0f, -0.04f, 0f), pr - 0.05f, 0.04f, mPlanks);
            deck.GetComponent<MeshRenderer>().sharedMaterial = Kit.Mat("deckplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), new Vector2(10f, 10f));
            Kit.Post(g, "Trim", new Vector3(0f, -0.16f, 0f), pr + 0.08f, 0.12f, mWhite, false);

            // White pontoon floats around the rim, like the picture.
            var pontoon = Kit.Mat("pontoon", new Color(0.95f, 0.95f, 0.93f), 0.4f);
            for (int i = 0; i < 14; i++)
            {
                float a = i * 360f / 14f;
                float rad = a * Mathf.Deg2Rad;
                var p = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (pr + 0.35f) + Vector3.up * (SeaY + 0.1f);
                Kit.Prim(PrimitiveType.Capsule, g, "Pontoon", p, new Vector3(1.1f, 2.6f, 1.1f), pontoon, false, new Vector3(90f, a + 90f, 0f));
            }

            // Lamp posts round the edge - except where a walkway comes onto the deck (those get a
            // pair of their own, one either side, when the walkway is built).
            var entries = new List<Vector3>();
            foreach (var d in _site.Docks)
            {
                var w = d - _site.Center; w.y = 0f;
                if (w.sqrMagnitude > 1f) entries.Add(_island.InverseTransformDirection(w.normalized));
            }
            for (int i = 0; i < 6; i++)
            {
                float rad = LampAngle(i) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
                bool inEntrance = false;
                foreach (var e in entries) if (Vector3.Angle(dir, e) < 14f) inEntrance = true;
                if (inEntrance) continue;
                DeckLampPost(g, dir * (pr - 0.6f));
            }

            ChooseOutdoorSpots();
            PlantTrees(g);

            IslandLandingPoint = _site.Local(ArrivalLocal.x, ArrivalLocal.y, ArrivalLocal.z);
        }

        private static float LampAngle(int i) => i * 60f + 30f;

        // Where on the deck the wind turbine and the water tanks stand (platform-local angles):
        // their usual spots, unless a walkway comes onto the deck there - then the nearest clear
        // spot, away from every walkway, the dumpster, the trash can and the front of the dome
        // (where employees wait).
        private static float _turbineAz = 104f, _tankAz = 109f, _woodsAz = 111f, _tankSide = 1f;

        // The three water tanks (platform-local): a row starting just beside the turbine's base
        // and angling in towards the dome.
        private static List<Vector3> TankSpots()
        {
            var spots = new List<Vector3>();
            for (int i = 0; i < 3; i++)
            {
                float a = (_turbineAz + _tankSide * (5f + i * 4.7f)) * Mathf.Deg2Rad;
                float r = TurbineRadius - 0.9f - i * 0.45f;
                spots.Add(new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r));
            }
            return spots;
        }
        private const float TurbineRadius = 15f;
        private static void ChooseOutdoorSpots()
        {
            var entries = new List<float>();
            foreach (var d in _site.Docks)
            {
                var l = _island.InverseTransformPoint(d);
                entries.Add(Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg);
            }
            var avoid = new List<(float angle, float width)>
            {
                (-135f, 14f),                                                                           // dumpster
                (Mathf.Atan2(TrashCanSpot.x, TrashCanSpot.y) * Mathf.Rad2Deg, 12f),                    // trash can
                (180f, 45f),                                                                            // front of the dome
            };
            foreach (var e in entries) avoid.Add((e, 18f));

            float Pick(float preferred, float halfWidth)
            {
                for (float off = 0f; off <= 180f; off += 15f)
                    foreach (float a in new[] { preferred + off, preferred - off })
                    {
                        bool clear = true;
                        foreach (var (angle, width) in avoid)
                            if (Mathf.Abs(Mathf.DeltaAngle(a, angle)) < width + halfWidth) { clear = false; break; }
                        if (clear) return a;
                    }
                return preferred;
            }
            _turbineAz = Pick(104f, 4f);
            // (Where a turbine with the tanks beside it would have gone - that's trees now.)
            _woodsAz = Pick(111f, 14f);
            // The water tanks: a short row beside the turbine, angling in towards the dome, on
            // whichever side is further from the walkways (and the dome doors facing them).
            float Room(float a) { float m = 999f; foreach (var e in entries) m = Mathf.Min(m, Mathf.Abs(Mathf.DeltaAngle(a, e))); return m; }
            _tankSide = Room(_turbineAz + 10f) >= Room(_turbineAz - 10f) ? 1f : -1f;
            _tankAz = _turbineAz + _tankSide * 10f;
            Core.Log.Msg($"Deck layout: wind turbine at {_turbineAz:0} deg, water tanks at {_tankAz:0} deg, extra trees round {_woodsAz:0} deg.");
        }

        // A cone-shaped ramp off a landing's edge at (cx, cz), walkway-local, from topY there
        // down to the ground at radius r. Directions are angles: 0 = straight out sideways (+x if
        // sx is 1, -x if -1), 90 = back (-z), -90 = forward (+z); it covers degFrom..degTo.
        // endY gives the height it comes down to in each direction (default landY). A quarter
        // (0..90) joins a flight off the side to one off the back.
        private static void CornerRamp(Transform g, float cx, float cz, float sx, float r, float topY, float landY,
                                       float degFrom = 0f, float degTo = 90f, System.Func<float, float> endY = null)
        {
            float End(float deg) => endY != null ? endY(deg) : landY;
            Vector3 At(float rad, float deg, float y) =>
                new Vector3(cx + sx * Mathf.Cos(deg * Mathf.Deg2Rad) * rad, y, cz - Mathf.Sin(deg * Mathf.Deg2Rad) * rad);
            float range = degTo - degFrom;
            const int rings = 8;
            int spokes = Mathf.Max(4, Mathf.RoundToInt(range / 5f));
            float Deg(int j) => degFrom + range * j / spokes;
            var v = new List<Vector3>();
            var t = new List<int>();
            void Tri(int i0, int i1, int i2, Vector3 want)
            {
                var n = Vector3.Cross(v[i1] - v[i0], v[i2] - v[i0]);
                if (n.sqrMagnitude < 1e-10f) return;
                if (Vector3.Dot(n, want) >= 0f) { t.Add(i0); t.Add(i1); t.Add(i2); }
                else { t.Add(i0); t.Add(i2); t.Add(i1); }
            }
            for (int k = 0; k <= rings; k++)
                for (int j = 0; j <= spokes; j++)
                    v.Add(At(r * k / rings, Deg(j), Mathf.Lerp(topY, End(Deg(j)), (float)k / rings)));
            for (int k = 0; k < rings; k++)
                for (int j = 0; j < spokes; j++)
                {
                    int a = k * (spokes + 1) + j, b = a + 1, c = a + spokes + 1, d = c + 1;
                    Tri(a, c, b, Vector3.up);
                    Tri(b, c, d, Vector3.up);
                }
            // A little edge round the bottom, down into the ground.
            var centre = new Vector3(cx, 0f, cz);
            for (int j = 0; j < spokes; j++)
            {
                float d0 = Deg(j), d1 = Deg(j + 1);
                int s = v.Count;
                v.Add(At(r, d0, End(d0))); v.Add(At(r, d1, End(d1)));
                v.Add(At(r, d0, End(d0) - 0.15f)); v.Add(At(r, d1, End(d1) - 0.15f));
                var outward = At(1f, (d0 + d1) * 0.5f, 0f) - centre;
                Tri(s, s + 2, s + 1, outward);
                Tri(s + 1, s + 2, s + 3, outward);
            }
            var uv = new List<Vector2>();
            foreach (var p in v) uv.Add(new Vector2(p.x / 2.6f, p.z / 1.6f));
            var mesh = new Mesh { name = "CornerRamp" };
            mesh.vertices = v.ToArray();
            mesh.uv = uv.ToArray();
            mesh.triangles = t.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            Kit.MeshObject(g, "CornerRamp", mesh, new[] { Kit.Mat("walkplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), Vector2.one) }, true);
            // Tread strips curving round it, level with the steps either side.
            int steps = Mathf.Max(1, Mathf.RoundToInt((topY - End(degFrom + range * 0.5f)) / 0.22f));
            for (int st = 0; st < steps; st++)
            {
                float k = (st + 0.5f) / steps, rad = r * k;
                if (rad < 0.25f) continue;
                int segs = Mathf.Max(2, Mathf.CeilToInt(rad * range * Mathf.Deg2Rad / 0.4f));
                float span = range / segs;
                for (int j = 0; j < segs; j++)
                {
                    float mid = degFrom + span * (j + 0.5f);
                    Kit.Box(g, "Tread", At(rad, mid, Mathf.Lerp(topY, End(mid), k) + 0.03f),
                            new Vector3(2f * rad * Mathf.Sin(span * 0.5f * Mathf.Deg2Rad) + 0.02f, 0.06f, 0.18f), mDarkWood, false,
                            Quaternion.LookRotation(At(1f, mid, 0f) - centre).eulerAngles);
                }
            }
        }

        // A metal handrail from a to b (its top, 1 m up) on balusters about a metre apart.
        private static void Handrail(Transform g, Vector3 a, Vector3 b)
        {
            float len = Vector3.Distance(a, b);
            if (len < 0.3f) return;
            Kit.Box(g, "Handrail", (a + b) * 0.5f, new Vector3(0.08f, 0.08f, len), mMetal, true, Quaternion.LookRotation(b - a).eulerAngles);
            int n = Mathf.Max(1, Mathf.RoundToInt(len));
            for (int k = 0; k <= n; k++)
            {
                var p = Vector3.Lerp(a, b, (float)k / n);
                Kit.Post(g, "Baluster", new Vector3(p.x, p.y - 1f, p.z), 0.04f, 1f, mMetal, false);
            }
        }

        // A wooden lamp post with a glowing lantern on top, standing on the deck at localPos.
        private static void DeckLampPost(Transform parent, Vector3 localPos)
        {
            var lamp = Kit.Group(parent, "DeckLamp", localPos).transform;
            Kit.Post(lamp, "Post", Vector3.zero, 0.07f, 1.6f, mDarkWood, false);
            // A round glowing globe on top, like the lamps hung in the dome.
            Kit.Ball(lamp, "Lantern", new Vector3(0f, 1.78f, 0f), new Vector3(0.48f, 0.34f, 0.48f), mLampGlow, false);
            _nightLights.Add(Kit.AddLight(lamp, new Vector3(0f, 1.7f, 0f), new Color(1f, 0.85f, 0.6f), 9f, 1.6f));
        }

        // Where employees wait: a group on the deck to the left of the entrance (as you face the
        // door from outside), keeping the doorway itself clear. If there are more employees than
        // fit there, the rest wait on the right-hand side of the door.
        public static List<Vector3> IdleSpots(int count)
        {
            var spots = new List<Vector3>();
            foreach (float side in new[] { -1f, 1f })
            {
                for (int row = 0; row < 4; row++)
                for (int col = 0; col < 8; col++)
                {
                    if (spots.Count >= count) return spots;
                    var p = new Vector3(side * (2.2f + col * 1.3f), 0.1f, -(DomeRadius + 1.5f) - row * 1.3f);
                    float r = new Vector2(p.x, p.z).magnitude;
                    if (r < DomeRadius + 1.2f || r > PlatformRadius - 1.6f) continue;
                    if (new Vector2(p.x + 11f, p.z + 11f).magnitude < 3.2f) continue;            // skip bin (about 4 m long)
                    if (new Vector2(p.x - TrashCanSpot.x, p.z - TrashCanSpot.y).magnitude < 1.8f) continue; // cleaners' trash can
                    if (side > 0f && new Vector2(p.x - 5f, p.z + PlatformRadius - 2f).magnitude < 1.5f) continue; // for-sale sign
                    if (new Vector2(p.x - ArrivalLocal.x, p.z - ArrivalLocal.z).magnitude < 1.2f) continue;
                    spots.Add(p);
                }
            }
            // Very large teams: carry on round the back of the dome.
            for (int i = 0; spots.Count < count; i++)
            {
                float a = (i * 13f - 60f) * Mathf.Deg2Rad;
                spots.Add(new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * IdleRadius + Vector3.up * 0.1f);
            }
            return spots;
        }

        // New hires step onto the deck here: front edge, right of the door.
        public static readonly Vector3 ArrivalLocal = new Vector3(2.5f, 0.1f, -PlatformRadius + 1.5f);

        // A ring of the game's own trees round the outer edge of the deck, leaving gaps for the
        // door side, the walkways, the lanterns, the wind turbine and the dumpster.
        private static void PlantTrees(Transform g)
        {
            var prefabs = GameTreePrefabs();
            var rng = new System.Random(31);
            float ringR = PlatformRadius - 1.5f;

            // Directions (platform-local angles) to keep clear.
            // (Wide gap at the front: that's where employees wait, either side of the door.)
            var keepClear = new List<(float angle, float width)> { (180f, 42f), (_turbineAz, 15f), (-135f, 9f) };
            for (int i = 0; i < 6; i++) keepClear.Add((LampAngle(i), 5f));
            var walkways = new List<float>();
            foreach (var dock in _site.Docks)
            {
                var local = _island.InverseTransformPoint(dock);
                walkways.Add(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg);   // walkway, its lamps and any loading bays by it
            }

            // Two rings: round the edge, and a second one in between them, nearer the dome. The
            // inner one keeps a wider berth of the walkways (where they come onto the deck and
            // the loading bay), the front where employees wait, and the water tanks.
            var spots = new List<(float angle, float radius, float height, bool front, bool entrance)>();
            for (float a = 6f; a < 360f; a += 12f) spots.Add((a, ringR, 6f, false, false));
            for (float a = 0f; a < 360f; a += 12f) spots.Add((a, DomeRadius + 2.4f, 5f, false, false));
            // And a few on the stretch from the windmill (past its tanks) towards the front: these
            // go by where things actually are - clear of every spot an employee waits on, the
            // trash can, the tanks, the turbine and the lamps - rather than the front's wide gap.
            foreach (float d in new[] { 17f, 22f, 27f, 32f })
            {
                spots.Add((_turbineAz + _tankSide * d, PlatformRadius - 1.1f, 5f, true, false));
                spots.Add((_turbineAz + _tankSide * (d + 2.5f), DomeRadius + 2.1f, 4.5f, true, false));
            }
            // The windmill side is wooded more thickly: extra trees in between both rings there,
            // either side of the turbine and its water tanks.
            // Likewise a small wood where the turbine stood for a while.
            for (float a = 0f; a < 360f; a += 12f)
            {
                float off = Mathf.Abs(Mathf.DeltaAngle(a, _turbineAz)), wood = Mathf.Abs(Mathf.DeltaAngle(a, _woodsAz));
                if ((off < 22f || off > 85f) && wood > 20f) continue;
                spots.Add((a, PlatformRadius - 0.9f, 5f, false, false));
                spots.Add((a + 6f, DomeRadius + 1.8f, 4.5f, false, false));
            }

            // What the front trees keep clear of.
            Vector3 Deck(float az, float r) => new Vector3(Mathf.Sin(az * Mathf.Deg2Rad), 0f, Mathf.Cos(az * Mathf.Deg2Rad)) * r;

            // The Docks side: more trees flanking each bridge as it comes onto the deck - a couple
            // of rows either side, close in but leaving the path itself, its lamps and the deck
            // loading bay clear (see keepAway below).
            foreach (float w in walkways)
                foreach (float sign in new[] { -1f, 1f })
                {
                    foreach (float off in new[] { 15f, 19f, 24f, 30f, 37f })
                        spots.Add((w + sign * off, PlatformRadius - 1.3f, 6f, true, true));
                    foreach (float off in new[] { 17f, 25f, 33f })
                        spots.Add((w + sign * off, DomeRadius + 2.6f, 5f, true, true));
                }

            var keepAway = new List<(Vector3 at, float gap)>
            {
                (Deck(_turbineAz, TurbineRadius), 2.6f),
                (new Vector3(TrashCanSpot.x, 0f, TrashCanSpot.y), 2.2f),
            };
            // Each bridge's path across the deck to the dome (3 m wide with a lamp either side).
            foreach (float w in walkways)
                for (float r = 6f; r <= PlatformRadius; r += 2f) keepAway.Add((Deck(w, r), 2.6f));
            // The loading bay on the deck where the first bridge comes in (see AddDeckBays).
            if (_site.Docks.Count > 0)
            {
                var l = _island.InverseTransformPoint(_site.Docks[0]); l.y = 0f;
                var outward = l.normalized;
                var right = Vector3.Cross(Vector3.up, -outward);
                var bay = outward * 15.2f + right * 3.7f;
                for (float t = -3f; t <= 3f; t += 1.5f) keepAway.Add((bay + right * t, 2.4f));
            }
            foreach (var p in TankSpots()) keepAway.Add((p, 1.5f));
            foreach (var p in IdleSpots(30)) keepAway.Add((new Vector3(p.x, 0f, p.z), 1.6f));
            for (int i = 0; i < 6; i++) keepAway.Add((Deck(LampAngle(i), PlatformRadius - 0.6f), 1.2f));

            int planted = 0;
            var placed = new List<Vector3>();
            foreach (var (a, ring, minH, front, entrance) in spots)
            {
                bool inner = ring < ringR;
                bool blocked = false;
                foreach (var (angle, width) in keepClear)
                {
                    if (front && angle == 180f) continue;   // (checked spot by spot below instead)
                    if (Mathf.Abs(Mathf.DeltaAngle(a, angle)) < width + (inner && angle == 180f ? 18f : 0f)) { blocked = true; break; }
                }
                foreach (float w in walkways)
                    if (Mathf.Abs(Mathf.DeltaAngle(a, w)) < (entrance ? 12f : inner ? 30f : 22f)) blocked = true;
                if (inner && !front && Mathf.Abs(Mathf.DeltaAngle(a, _tankAz)) < 20f) blocked = true;   // the water tanks
                if (blocked) continue;

                float rad = a * Mathf.Deg2Rad;
                var pos = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (ring + (float)rng.NextDouble() * (front ? 0.3f : 0.6f));
                if (front)
                {
                    bool tooClose = false;
                    foreach (var (at, gap) in keepAway) if (Vector3.Distance(pos, at) < gap) { tooClose = true; break; }
                    foreach (var t in placed) if (Vector3.Distance(pos, t) < 2f) { tooClose = true; break; }
                    if (tooClose) continue;
                }
                placed.Add(pos);
                if (prefabs.Count > 0)
                {
                    var tree = Object.Instantiate(prefabs[rng.Next(prefabs.Count)], g);
                    tree.name = "GameTree";
                    tree.SetActive(true);
                    tree.transform.localPosition = pos;
                    tree.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                    tree.transform.localScale = Vector3.one;
                    // Size it to a deck-friendly 6-9 m.
                    float h = 0f;
                    foreach (var r in tree.GetComponentsInChildren<Renderer>(true)) h = Mathf.Max(h, r.bounds.max.y - tree.transform.position.y);
                    float target = minH + (float)rng.NextDouble() * 3f;
                    if (h > 0.5f) tree.transform.localScale = Vector3.one * (target / h);
FixTreeBark(tree);
                }
                else
                {
                    Palm(g, pos, a, 0.7f, rng.Next());
                }
                planted++;
            }
            Core.Log.Msg($"Planted {planted} {(prefabs.Count > 0 ? "game trees" : "palms (no game trees found)")} round the deck.");
        }

        // Some of the game's tree models lose their trunk colour when copied out of the terrain
        // (the terrain normally feeds them per-tree colour), leaving white trunks. Give any
        // bark/trunk material our bark instead.
        private static bool _barkLogged;
        private static void FixTreeBark(GameObject tree)
        {
            var names = new List<string>();
            foreach (var r in tree.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    string n = (m.name + " " + r.gameObject.name).ToLowerInvariant();
                    names.Add(m.name);
                    if (n.Contains("bark") || n.Contains("trunk") || n.Contains("wood") || n.Contains("stem") || n.Contains("tree_body"))
                    {
                        mats[i] = mBark;
                        changed = true;
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }
            if (!_barkLogged)
            {
                _barkLogged = true;
                Core.Log.Msg("Tree materials: " + string.Join(", ", names));
            }
        }

        // The tree models the game's own terrain uses, else tree-looking objects in the scene.
        private static List<GameObject> GameTreePrefabs()
        {
            var list = new List<GameObject>();
            var seen = new HashSet<System.IntPtr>();
            try
            {
                foreach (var t in Terrain.activeTerrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    foreach (var proto in t.terrainData.treePrototypes)
                        if (proto != null && proto.prefab != null && seen.Add(proto.prefab.Pointer)) list.Add(proto.prefab);
                }
            }
            catch { }
            if (list.Count > 0) return list;

            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || !go.scene.IsValid() || list.Count >= 8) continue;
                string n = go.name.ToLowerInvariant();
                if (!(n.Contains("tree") || n.Contains("pine")) || n.Contains("street")) continue;
                if (go.GetComponent<LODGroup>() == null && go.GetComponent<MeshRenderer>() == null) continue;
                var r = go.GetComponentInChildren<Renderer>();
                if (r == null || r.bounds.size.y < 4f || r.bounds.size.y > 30f) continue;
                if (seen.Add(go.Pointer)) list.Add(go);
            }
            return list;
        }

        // ================================================================ walkways from the shore

        public sealed class DockInfo
        {
            public string Name;
            public Vector3 Stand;   // top of the walkway, on land
            public Vector3 Portal;  // on the walkway where it lies on the quay: the island's walkable
                                    // area and the game's overlap here (see IslandNavMesh)
        }

        private static readonly List<DockInfo> _docks = new List<DockInfo>();
        public static IReadOnlyList<DockInfo> Docks => _docks;
        public static Transform Root => _root != null ? _root.transform : null;

        // Marker other mods look for: where an NPC can step between the game's walkable area and
        // the island's (area IslandNavMesh.IslandArea) without moving.
        public const string PortalMarkerName = "NavPortal_29";

        // One walkway per shore point: walk from the point towards the platform to the edge of
        // the land (a quay can be 10 m above the water), then stairs down from the edge and a
        // floating wooden walkway straight onto the platform deck. Built in world space.
        private static void BuildDocks()
        {
            _docks.Clear();
            _site.DockSpans.Clear();
            for (int i = 0; i < _site.Docks.Count; i++)
            {
                // Loading bays 1 and 2 on the road by walkway 2, 3 and 4 on the deck where walkway 1 comes in;
                // none for walkway 3 (the bridge to the warehouse).
                var info = BuildDock(_site.Docks[i], "walkway " + (i + 1), i == 0 ? BayPlace.Deck : i == 1 ? BayPlace.Road : BayPlace.None);
                if (info != null) _docks.Add(info);
            }
        }

        private enum BayPlace { None, Road, Deck }

        private static DockInfo BuildDock(Vector3 shore, string name, BayPlace bays)
        {
            float sea = _site.SeaLevel;
            var dir = _site.Center - shore; dir.y = 0f;
            if (dir.sqrMagnitude < 1f) return null;
            dir.Normalize();
            var ignore = _root.transform;

            bool Land(Vector3 p, out float h)
            {
                var gh = Siting.GroundHeight(p.x, p.z, ignore);
                h = gh ?? float.NegativeInfinity;
                return gh.HasValue && gh.Value > sea + 0.3f;
            }

            // If the point was picked out on the water, walk back to the shore first.
            var start = shore;
            if (!Land(start, out _))
            {
                bool found = false;
                for (float t = 1f; t <= 60f; t += 1f)
                    if (Land(shore - dir * t, out _)) { start = shore - dir * t; found = true; break; }
                if (!found)
                {
                    Core.Log.Warning($"The {name} point isn't near any land - skipped.");
                    return null;
                }
            }

            // Edge of the land towards the platform.
            Vector3 edge = start;
            float edgeY = 0f;
            bool reachedWater = false;
            for (float t = 0f; t <= 300f; t += 0.5f)
            {
                var p = start + dir * t;
                if (Land(p, out float h)) { edge = p; edgeY = h; }
                else if (t > 0.5f) { reachedWater = true; break; }
            }
            if (!reachedWater)
            {
                Core.Log.Warning($"No water between the {name} point and the platform - skipped.");
                return null;
            }
            edge.y = edgeY;

            var g = Kit.Group(_root.transform, "Walkway_" + name, Vector3.zero).transform;
            g.position = edge;
            g.rotation = Quaternion.LookRotation(dir);

            // Local frame: z out over the water towards the platform, y relative to the edge.
            float deckY = (sea + Site.DeckAboveSea) - edgeY;
            var toCenter = _site.Center - edge; toCenter.y = 0f;
            float toPlatform = toCenter.magnitude - PlatformRadius;

            // Profile of the walkway as (z, y) points. On land it sits a few cm proud of the
            // ground so the planks never flicker against the pavement. If there's a railing along
            // the quay edge, it steps up over it on a little landing first.
            const float landY = 0.04f;
            float railTop = RailingTop(g, out float railZ);
            var pts = new List<Vector2>();
            float descentStart, topY;
            bool sideStairs = false;   // stairs off both ends of the landing, along the quay path
            float sideLen = 0f;
            float rampUp = 0f;         // ramp walkways: how long the ramp up from the quay is
            // Side stairs run along the quay just inland of the railing (not between it and the water).
            float stairNear = railZ - 0.15f, stairFar = stairNear - 1.2f;
            if (railTop > 0.25f)
            {
                topY = Mathf.Min(railTop + 0.15f, 2.2f);
                float upLen = Mathf.Max(1f, topY / 0.6f);
                // (Only the warehouse bridge - walkway 3 - lands on a narrow path against a
                // building; the others keep their ramp.)
                if (name != "walkway 3" || Clear(g, new Vector3(-1.3f, landY + 0.35f, -2.6f - upLen - 0.8f), new Vector3(1.3f, topY + 1.9f, -0.3f), edgeY, 6f))
                {
                    // Room behind the railing: up a ramp from the quay, over the railing.
                    pts.Add(new Vector2(-2.6f - upLen - 0.8f, landY));
                    pts.Add(new Vector2(-2.6f - upLen, landY));
                    pts.Add(new Vector2(-2.6f, topY));
                    pts.Add(new Vector2(0.9f, topY));
                    rampUp = upLen;
                    Core.Log.Msg($"The {name} steps over a {railTop:0.00} m railing at the quay edge.");
                }
                else
                {
                    // A building right behind the railing (a narrow quay path): the walkway stops
                    // on a landing over the railing, with stairs off it sideways along the path.
                    // Stairs go down both ends of the landing, so you can get on from either way
                    // along the path.
                    sideLen = upLen + 0.6f;
                    sideStairs = true;
                    pts.Add(new Vector2(stairFar, topY));   // the landing reaches back over the railing to the stairs
                    pts.Add(new Vector2(0.9f, topY));
                    Core.Log.Msg($"The {name} stops on a landing over a {railTop:0.00} m railing, with stairs down to the quay path at both ends.");
                }
                descentStart = 0.9f;
            }
            else
            {
                topY = landY;
                pts.Add(new Vector2(-2f, landY));
                pts.Add(new Vector2(0.3f, landY));
                descentStart = 0.3f;
            }
            float rise = Mathf.Abs(topY - deckY);
            float stairLen = Mathf.Clamp(rise / 0.6f, 1.5f, Mathf.Max(1.5f, toPlatform + 0.7f - descentStart));
            if (rise / stairLen > 1f) Core.Log.Warning($"The {name} stairs are very steep - the platform is close to a high quay.");
            pts.Add(new Vector2(descentStart + stairLen, deckY));

            // Walkable surface, with a tread for every step on the sloped parts, and handrails.
            var path = new List<Vector3>();
            foreach (var pt in pts) path.Add(new Vector3(0f, pt.y, pt.x));
            Kit.MeshObject(g, "Ramp", Kit.Ribbon(path, 1.3f, 0.25f, 1.6f), new[] { Kit.Mat("walkplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), Vector2.one) }, true);
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a2 = pts[i - 1], b2 = pts[i];
                float dy = b2.y - a2.y;
                if (Mathf.Abs(dy) > 0.05f)
                {
                    int steps = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(dy) / 0.22f));
                    for (int s = 0; s < steps; s++)
                    {
                        float k = (s + 0.5f) / steps;
                        Kit.Box(g, "Tread", new Vector3(0f, Mathf.Lerp(a2.y, b2.y, k) + 0.03f, Mathf.Lerp(a2.x, b2.x, k)), new Vector3(2.5f, 0.06f, 0.18f), mDarkWood, false);
                    }
                }
                foreach (float x in new[] { -1.25f, 1.25f })
                {
                    // (No handrails along the landing: the side stairs go off both its ends.)
                    if (sideStairs && i == 1) continue;
                    var a = new Vector3(x, a2.y + 1f, a2.x);
                    var b = new Vector3(x, b2.y + 1f, b2.x);
                    // Handrails only where that side is over the water. Over land it's left open,
                    // so you can hop on and off the stairs there.
                    int m = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a2, b2) / 0.75f));
                    int runFrom = -1;
                    for (int q = 0; q <= m; q++)
                    {
                        bool water = q < m && !Land(g.TransformPoint(new Vector3(x * 1.3f, 0f, Mathf.Lerp(a2.x, b2.x, (q + 0.5f) / m))), out _);
                        if (water && runFrom < 0) runFrom = q;
                        else if (!water && runFrom >= 0)
                        {
                            Handrail(g, Vector3.Lerp(a, b, (float)runFrom / m), Vector3.Lerp(a, b, (float)q / m));
                            runFrom = -1;
                        }
                    }
                }
            }
            // Side stairs: from each end of the landing down along the quay path to the ground
            // (and up again on the way back). No handrails - they sit right against the railing.
            var clearOfBins = new List<(Vector3 a, Vector3 b)>();
            if (sideStairs)
            foreach (float sideExit in new[] { 1f, -1f })
            {
                float x0 = sideExit * 1.3f, x1 = sideExit * (1.3f + sideLen - 0.6f), x2 = sideExit * (1.3f + sideLen);
                // From the building side of the landing out to the quay railing. The quay isn't
                // square to the bridge, so along the path the railing can be further off than it
                // is under the landing - the flight widens to fill that gap.
                float zOuter = Mathf.Max(stairNear, RailingAlong(g, x0, x2, stairNear, landY, stairNear + 0.1f) - 0.1f);
                float width = zOuter - stairFar, zc = (zOuter + stairFar) * 0.5f;
                var sidePath = new List<Vector3> { new Vector3(x0, topY, zc), new Vector3(x1, landY, zc), new Vector3(x2, landY, zc) };
                Kit.MeshObject(g, "SideStairs", Kit.Ribbon(sidePath, width * 0.5f, 0.25f, 1.6f), new[] { Kit.Mat("walkplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), Vector2.one) }, true);
                int steps = Mathf.Max(1, Mathf.RoundToInt((topY - landY) / 0.22f));
                for (int s = 0; s < steps; s++)
                {
                    float k = (s + 0.5f) / steps;
                    Kit.Box(g, "Tread", new Vector3(Mathf.Lerp(x0, x1, k), Mathf.Lerp(topY, landY, k) + 0.03f, zc), new Vector3(0.18f, 0.06f, width), mDarkWood, false);
                }
                // The right-hand flight runs out past the end of the quay over the water: a
                // handrail on its water side (only).
                if (sideExit > 0f)
                    Handrail(g, new Vector3(x0, topY + 1f, zOuter - 0.05f), new Vector3(x2, landY + 1f, zOuter - 0.05f));
                Core.Log.Msg($"The {name} {(sideExit > 0 ? "right" : "left")}-hand stairs are {width:0.0} m wide (out to the quay railing).");
                clearOfBins.Add((new Vector3(x0, landY + 0.2f, stairFar - 0.1f), new Vector3(x2 + sideExit * 0.3f, topY + 1f, zOuter)));
            }
            // And a flight straight down off the back of the landing onto the quay path, no rails.
            if (sideStairs)
            {
                float z0 = stairFar, z1 = stairFar - (sideLen - 0.6f), z2 = z1 - 0.6f;
                var backPath = new List<Vector3> { new Vector3(0f, topY, z0), new Vector3(0f, landY, z1), new Vector3(0f, landY, z2) };
                Kit.MeshObject(g, "BackStairs", Kit.Ribbon(backPath, 1.2f, 0.25f, 1.6f), new[] { Kit.Mat("walkplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), Vector2.one) }, true);
                int steps = Mathf.Max(1, Mathf.RoundToInt((topY - landY) / 0.22f));
                for (int s = 0; s < steps; s++)
                {
                    float k = (s + 0.5f) / steps;
                    Kit.Box(g, "Tread", new Vector3(0f, Mathf.Lerp(topY, landY, k) + 0.03f, Mathf.Lerp(z0, z1, k)), new Vector3(2.3f, 0.06f, 0.18f), mDarkWood, false);
                }
                clearOfBins.Add((new Vector3(-1.5f, landY + 0.2f, z2 - 0.3f), new Vector3(1.5f, topY + 1f, stairFar)));

                // The corners between the back stairs and the flights either side are ramps that
                // sweep round from one to the other, so you can walk off them any way.
                float fanR = sideLen - 0.6f;
                foreach (float sx in new[] { -1f, 1f })
                {
                    CornerRamp(g, sx * 1.25f, stairFar, sx, fanR, topY, landY);
                    clearOfBins.Add((new Vector3(sx * 1.2f, landY + 0.2f, stairFar - fanR - 0.3f), new Vector3(sx * (1.25f + fanR + 0.3f), topY + 1f, stairFar)));
                }
            }
            // Ramp walkways: a ramp straight down off the side of the landing (the edge over the
            // quay, where the handrail used to be) to the ground, as wide as the clear stretch of
            // that edge - so you can hop on and off there, not just up the ramp at the back.
            if (rampUp > 0f)
            {
                float run = Mathf.Max(2.5f, (topY - landY) / 0.42f);
                const float slice = 0.25f;
                float bestW = 0f, bestSx = 0f, bestA = 0f, bestB = 0f, bestEnd = 0f;
                foreach (float sx in new[] { -1f, 1f })
                {
                    // Every 25 cm along the landing's edge: clear ground straight out from there?
                    float runA = 0f, runEnd = float.MaxValue;
                    bool inRun = false;
                    for (float z = -2.6f + slice * 0.5f; ; z += slice)
                    {
                        float? end = z < 0.9f ? FanSpoke(g, sx * 1.3f, z, sx, 0f, run, topY, landY, sea, edgeY) : null;
                        if (end.HasValue)
                        {
                            if (!inRun) { inRun = true; runA = z - slice * 0.5f; runEnd = float.MaxValue; }
                            runEnd = Mathf.Min(runEnd, end.Value);
                            continue;
                        }
                        if (inRun)
                        {
                            float b = z - slice * 0.5f;
                            if (b - runA > bestW) { bestW = b - runA; bestSx = sx; bestA = runA; bestB = b; bestEnd = runEnd; }
                            inRun = false;
                        }
                        if (z >= 0.9f) break;
                    }
                }
                if (bestW >= 1f)
                {
                    // Twice that wide, along the landing's edge back towards the ramp (never
                    // forward - that way is the railing and the water). Where the ground beside it
                    // is higher, the ramp just runs into it.
                    bestA = Mathf.Max(-2.6f, bestB - bestW * 2f);
                    bestW = bestB - bestA;
                    float x0 = bestSx * 1.3f, x1 = bestSx * (1.3f + run), x2 = bestSx * (1.3f + run + 0.6f), zc = (bestA + bestB) * 0.5f;
                    var flight = new List<Vector3> { new Vector3(x0, topY, zc), new Vector3(x1, bestEnd, zc), new Vector3(x2, bestEnd, zc) };
                    Kit.MeshObject(g, "SideRamp", Kit.Ribbon(flight, bestW * 0.5f, 0.25f, 1.6f), new[] { Kit.Mat("walkplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), Vector2.one) }, true);
                    int steps = Mathf.Max(1, Mathf.RoundToInt((topY - bestEnd) / 0.22f));
                    for (int s = 0; s < steps; s++)
                    {
                        float k = (s + 0.5f) / steps;
                        Kit.Box(g, "Tread", new Vector3(Mathf.Lerp(x0, x1, k), Mathf.Lerp(topY, bestEnd, k) + 0.03f, zc), new Vector3(0.18f, 0.06f, bestW - 0.1f), mDarkWood, false);
                    }
                    clearOfBins.Add((new Vector3(x0, bestEnd + 0.2f, bestA), new Vector3(x2 + bestSx * 0.3f, topY + 1f, bestB)));
                    Core.Log.Msg($"The {name} has a ramp straight down off the {(bestSx > 0 ? "right" : "left")} of its landing ({bestW:0.0} m wide).");
                }
                else Core.Log.Msg($"The {name}: no clear ground beside the landing for a side ramp.");
            }

            // The game's own bins can't stand in the way of the stairs: any there are taken away.
            foreach (var (a, b) in clearOfBins) RemoveBins(g, a, b);
            // No railing found to step over, yet the game may have one without a collider here.
            if (railTop <= 0.25f) RemoveRailingsInPath(g, pts[0].x - 0.3f, descentStart + 0.6f, name);

            // A lamp post either side of the top of the bridge, where the stairs down to the
            // platform start.
            foreach (float x in new[] { -1.15f, 1.15f })
                DeckLampPost(g, new Vector3(x, topY, descentStart - 0.25f));


            for (float z = descentStart + 2.5f; z < descentStart + stairLen; z += 4f)
            {
                float y = Mathf.Lerp(topY, deckY, (z - descentStart) / stairLen) - 0.3f;
                float bottom = (sea - 5f) - edgeY;
                foreach (float x in new[] { -1.1f, 1.1f })
                    Kit.Post(g, "Piling", new Vector3(x, bottom, z), 0.15f, y - bottom, mDarkWood);
            }

            // Floating walkway from the bottom of the stairs onto the platform deck.
            // The walkway tucks 0.5 m under the platform deck, 1.5 cm lower, so the two surfaces never
            // fight over the same pixels where they meet.
            float wz0 = descentStart + stairLen, wz1 = toPlatform + 0.5f;
            var walkPlanks = Kit.Mat("walkplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), Vector2.one);
            if (wz1 - wz0 > 0.3f)
            {
                float len = wz1 - wz0, mid = (wz0 + wz1) * 0.5f;
                var walkPath = new List<Vector3> { new Vector3(0f, deckY - 0.015f, wz0), new Vector3(0f, deckY - 0.015f, wz1) };
                Kit.MeshObject(g, "Walkway", Kit.Ribbon(walkPath, 1.4f, 0.3f, 1.6f), new[] { walkPlanks }, true);
                var floats = Kit.Mat("pontoon", new Color(0.95f, 0.95f, 0.93f), 0.4f);
                foreach (float x in new[] { -1.1f, 1.1f })
                    Kit.Prim(PrimitiveType.Capsule, g, "Float", new Vector3(x, deckY - 0.5f, mid), new Vector3(0.6f, len * 0.5f, 0.6f), floats, false, new Vector3(90f, 0f, 0f));
                foreach (float x in new[] { -1.35f, 1.35f })
                {
                    Kit.Box(g, "Rail", new Vector3(x, deckY + 0.9f, mid), new Vector3(0.07f, 0.07f, len), mDarkWood, true);
                    for (float z = wz0; z <= wz1; z += 2.5f)
                        Kit.Post(g, "RailPost", new Vector3(x, deckY, z), 0.05f, 0.9f, mDarkWood, false);
                }
            }

            // A lamp post either side of where the walkway comes onto the deck (the same posts as
            // round the deck edge, which leave the walkway entrances clear).
            foreach (float x in new[] { -1.9f, 1.9f })
                DeckLampPost(g, new Vector3(x, deckY, toPlatform + 0.9f));
            // The long floating bridge by the deck loading bay: two more, on its rails halfway
            // along it.
            if (bays == BayPlace.Deck && toPlatform - wz0 > 3f)
                foreach (float x in new[] { -1.35f, 1.35f })
                    DeckLampPost(g, new Vector3(x, deckY - 0.015f, (wz0 + toPlatform) * 0.5f));

            if (bays == BayPlace.Road) AddParkingBays(g, sea, name);
            else if (bays == BayPlace.Deck) AddDeckBays(shore, name);

            _site.DockSpans.Add((edge, g.TransformPoint(new Vector3(0f, 0f, wz1))));

            // The handover spot: on the walkway's flat stretch on the quay, where its planks sit a
            // few cm above the pavement - so the island's walkable area and the game's overlap.
            // With side stairs that's the bottom of the back flight; otherwise the ramp's foot.
            Vector3 portalLocal = sideStairs
                ? new Vector3(0f, landY, stairFar - (sideLen - 0.6f) - 0.3f)
                : new Vector3(0f, landY, pts[0].x + 0.4f);
            var marker = new GameObject(PortalMarkerName);
            marker.transform.SetParent(g, false);
            marker.transform.localPosition = portalLocal;

            return new DockInfo { Name = name, Stand = g.TransformPoint(new Vector3(0f, 0.1f, pts[0].x)), Portal = g.TransformPoint(portalLocal) };
        }

        // ------------------------------------------------------------ parking bays

        // A parking bay by a walkway: where a vehicle parks (on the road) and which way it faces
        // (nose to the kerb). The property turns each into one of the game's loading docks, whose
        // own yellow bay markings are what you see.
        public sealed class ParkingBay
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public string Walkway;
            // On the deck: the bay's painted markings (not its parking spot) are centred on Position,
            // so the whole bay sits on the planks.
            public bool CentreMarkings;
        }
        public static readonly List<ParkingBay> ParkingBays = new List<ParkingBay>();
        private const float BayWidth = 3f, BayLength = 6f;

        private static float? Ground(Vector3 p) => Siting.GroundHeight(p.x, p.z, _root.transform);

        // Walking from start along dir: where the ground drops 6-40 cm off a kerb and stays down.
        private static bool FindKerb(Vector3 start, Vector3 dir, float maxDist, float sea, out float dist, out float top)
        {
            dist = 0f; top = 0f;
            float? prev = null;
            for (float t = 0.5f; t <= maxDist; t += 0.25f)
            {
                var p = start + dir * t;
                var h = Ground(p);
                if (!h.HasValue || h.Value < sea + 0.3f) { prev = null; continue; }
                if (prev.HasValue)
                {
                    var further = Ground(p + dir * 0.75f);
                    if (prev.Value - h.Value > 0.06f && prev.Value - h.Value < 0.4f && further.HasValue && prev.Value - further.Value > 0.06f)
                    { dist = t; top = prev.Value; return true; }
                }
                prev = h;
            }
            return false;
        }

        // Two bays side by side on the road at the nearest kerb to the top of a walkway's stairs,
        // nose to the kerb. First straight back inland along the walkway; if the road isn't there
        // (a grass bank, say), all round the top of the stairs. If the spot is uneven, has
        // something standing in it, or is by another property's loading bay, the pair slides
        // along the kerb until it fits.
        private static void AddParkingBays(Transform g, float sea, string name)
        {
            var top = g.position;
            var back = -g.forward; back.y = 0f; back.Normalize();
            Vector3 kerbPoint, normal, tangent;
            float kerbTop;

            if (FindKerb(top + back, back, 44f, sea, out float d0, out kerbTop))
            {
                kerbPoint = top + back + back * d0;
                normal = back;
                tangent = Vector3.Cross(Vector3.up, normal).normalized;
            }
            else
            {
                float best = float.MaxValue, bestAngle = 0f;
                for (float a = 0f; a < 360f; a += 10f)
                {
                    var dir = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
                    if (FindKerb(top, dir, 25f, sea, out float d, out float kt) && d < best) { best = d; bestAngle = a; kerbTop = kt; }
                }
                if (best == float.MaxValue)
                {
                    Core.Log.Warning($"Couldn't find a road near the {name} - no parking bays there.");
                    return;
                }
                FindKerb(top, Quaternion.Euler(0f, bestAngle, 0f) * Vector3.forward, 25f, sea, out _, out kerbTop);
                var mainDir = Quaternion.Euler(0f, bestAngle, 0f) * Vector3.forward;
                kerbPoint = top + mainDir * best;
                // The kerb's own line, from the kerb a little either side.
                tangent = Vector3.Cross(Vector3.up, mainDir).normalized;
                var dirA = Quaternion.Euler(0f, bestAngle - 8f, 0f) * Vector3.forward;
                var dirB = Quaternion.Euler(0f, bestAngle + 8f, 0f) * Vector3.forward;
                if (FindKerb(top, dirA, 30f, sea, out float da, out _) && FindKerb(top, dirB, 30f, sea, out float db, out _))
                {
                    var t2 = (top + dirB * db) - (top + dirA * da); t2.y = 0f;
                    if (t2.sqrMagnitude > 0.01f) tangent = t2.normalized;
                }
                normal = Vector3.Cross(tangent, Vector3.up).normalized;
                if (Vector3.Dot(normal, mainDir) < 0f) normal = -normal;
            }

            var rot = Quaternion.LookRotation(-normal, Vector3.up);   // nose to the kerb
            var centreLine = kerbPoint + normal * (0.3f + BayLength * 0.5f);

            bool Fits(Vector3 c, out float ground)
            {
                ground = 0f;
                var h0 = Ground(c);
                if (!h0.HasValue) return false;
                // Road can slope (round a bend, up a hill): allowed up to about 1 in 8, no steps.
                foreach (var (dx, dz) in new[] { (-1.4f, -2.9f), (1.4f, -2.9f), (-1.4f, 2.6f), (1.4f, 2.6f) })
                {
                    var h = Ground(c + rot * new Vector3(dx, 0f, dz));
                    if (!h.HasValue || Mathf.Abs(h.Value - h0.Value) > 0.25f + 0.13f * Mathf.Sqrt(dx * dx + dz * dz)) return false;
                }
                // Nothing standing in it (posts, bins, barriers). The ground itself doesn't count:
                // on a slope the road rises into the check box.
                foreach (var col in Physics.OverlapBox(new Vector3(c.x, h0.Value + 1.25f, c.z), new Vector3(BayWidth * 0.5f, 1f, BayLength * 0.5f), rot, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (col == null || col.transform.IsChildOf(_root.transform)) continue;
                    if (Siting.IsTerrain(col) || col.bounds.size.x > 15f || col.bounds.size.z > 15f) continue;   // ground, roads, pavements
                    return false;
                }
                // Not on or next to another property's loading bay.
                foreach (var p in Il2CppScheduleOne.Property.Property.Properties)
                {
                    if (p == null || p.LoadingDocks == null) continue;
                    foreach (var dk in p.LoadingDocks)
                    {
                        if (dk == null) continue;
                        var at = dk.Parking != null && dk.Parking.ParkingSpots != null && dk.Parking.ParkingSpots.Count > 0 ? dk.Parking.ParkingSpots[0].transform.position : dk.transform.position;
                        if (new Vector2(at.x - c.x, at.z - c.z).magnitude < 7f) return false;
                    }
                }
                ground = h0.Value;
                return true;
            }

            const float half = BayWidth * 0.5f + 0.1f;
            foreach (float slide in new[] { 0f, 1f, -1f, 2f, -2f, 3f, -3f, 4f, -4f, 5f, -5f, 6f, -6f, 8f, -8f, 10f, -10f })
            {
                var a = centreLine + tangent * (slide - half);
                var b = centreLine + tangent * (slide + half);
                if (!Fits(a, out float ga) || !Fits(b, out float gb)) continue;
                ParkingBays.Add(new ParkingBay { Position = new Vector3(a.x, ga, a.z), Rotation = rot, Walkway = name });
                ParkingBays.Add(new ParkingBay { Position = new Vector3(b.x, gb, b.z), Rotation = rot, Walkway = name });
                Core.Log.Msg($"Two parking bays on the road by the {name} (kerb {Vector3.Distance(top, kerbPoint):0.0} m from the top of the stairs{(slide != 0f ? $", {slide:+0;-0} m along it" : "")}).");
                return;
            }
            Core.Log.Warning($"No room for parking bays on the road by the {name}.");
        }

        // One bay on the platform deck where a walkway comes in, beside the way from the walkway to
        // the dome door facing it (on the right as you walk in), across the deck ring between the
        // dome and the edge.
        private static void AddDeckBays(Vector3 shore, string name)
        {
            var l = _island.InverseTransformPoint(shore); l.y = 0f;
            var outward = l.normalized;                        // from the middle towards the walkway
            var right = Vector3.Cross(Vector3.up, -outward);   // right, walking in towards the dome
            var local = outward * 15.2f + right * 3.7f;        // halfway across the deck ring
            ParkingBays.Add(new ParkingBay
            {
                Position = _island.TransformPoint(local),
                // Turned so its markings run out from the dome towards the edge (a bay's markings
                // lie across its parking spot's facing).
                Rotation = _island.rotation * Quaternion.LookRotation(right, Vector3.up),
                Walkway = name,
                CentreMarkings = true,
            });
            Core.Log.Msg($"A loading bay on the deck where the {name} comes in.");
        }

        // A dumpster / bin / skip of the game's (by its name or its parents').
        private static bool IsBin(Transform t)
        {
            for (int i = 0; t != null && i < 4; i++, t = t.parent)
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("dumpster") || n.Contains("skip") || n.Contains("trash") || n.Contains("garbage") || n.Contains("wheelie") || n == "bin" || n.StartsWith("bin ") || n.StartsWith("bin_") || n.Contains(" bin")) return true;
            }
            return false;
        }

        // Switches off any of the game's bins standing in the box from a to b (walkway-local).
        private static void RemoveBins(Transform g, Vector3 a, Vector3 b)
        {
            var centre = g.TransformPoint((a + b) * 0.5f);
            var half = new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z)) * 0.5f;
            foreach (var col in Physics.OverlapBox(centre, half, g.rotation, ~0, QueryTriggerInteraction.Ignore))
            {
                if (col == null || col.transform.IsChildOf(_root.transform) || !IsBin(col.transform)) continue;
                // The whole bin: the top-most object named like one.
                var top = col.transform;
                for (var t = col.transform.parent; t != null && IsBin(t); t = t.parent) top = t;
                if (!top.gameObject.activeSelf) continue;
                top.gameObject.SetActive(false);
                _removedBins.Add(top.gameObject);
                Core.Log.Msg($"Took away the game's '{top.name}' - it was standing in the warehouse bridge's stairs.");
            }
        }
        private static readonly List<GameObject> _removedBins = new List<GameObject>();

        // A walkway that found nothing to step over (railingTop said "no railing") runs at ground
        // level - but some of the game's railings have no collider, so they were never seen and
        // stick up through the planks. Switch off anything of the game's that stands 0.4-2.2 m
        // tall in the walkway's footprint on land (z from zFrom to zTo, walkway-local).
        private static void RemoveRailingsInPath(Transform g, float zFrom, float zTo, string name)
        {
            var probes = new List<Vector3>();
            for (float z = zFrom; z <= zTo; z += 0.05f)
            for (float x = -1.3f; x <= 1.31f; x += 0.1f)
            foreach (float y in new[] { 0.5f, 0.9f })
                probes.Add(g.TransformPoint(new Vector3(x, y, z)));

            float groundY = g.position.y;
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                if (r == null || !r.enabled || r.transform.IsChildOf(_root.transform)) continue;
                if (r.bounds.SqrDistance(g.position) > 400f) continue; // nowhere near
                var b = r.bounds;
                if (b.size.y > 2.2f || b.max.y - groundY < 0.4f || b.max.y - groundY > 2.2f) continue;
                if (!probes.Exists(p => b.Contains(p))) continue;
                // Never anything that moves or that the player placed: vehicles, NPCs, props.
                if (r.GetComponentInParent<Rigidbody>() != null) continue;
                if (r.GetComponentInParent<Il2CppScheduleOne.NPCs.NPC>() != null) continue;
                if (r.GetComponentInParent<Il2CppScheduleOne.PlayerScripts.Player>() != null) continue;
                if (r.GetComponentInParent<Il2CppScheduleOne.EntityFramework.BuildableItem>() != null) continue;

                r.gameObject.SetActive(false);
                _removedBins.Add(r.gameObject); // put back with the bins
                Core.Log.Msg($"Took away the game's '{r.name}' ({b.size.x:0.0} x {b.size.y:0.0} x {b.size.z:0.0} m) - it stood across the {name}.");
            }
        }

        // Nothing standing in the box from a to b (walkway-local corners), other than the ground
        // itself (anything whose top is below the quay edge + 35 cm) and the island's own parts.
        // minSize: only count things at least this big (e.g. buildings, not lamp posts).
        private static bool Clear(Transform g, Vector3 a, Vector3 b, float edgeY, float minSize = 0f)
        {
            var centre = g.TransformPoint((a + b) * 0.5f);
            var half = new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z)) * 0.5f;
            foreach (var col in Physics.OverlapBox(centre, half, g.rotation, ~0, QueryTriggerInteraction.Ignore))
            {
                if (col == null || col.transform.IsChildOf(_root.transform)) continue;
                if (col.bounds.max.y < edgeY + 0.35f || col.bounds.size.magnitude < minSize) continue;
                return false;
            }
            return true;
        }

        // Walkway-local z of the quay railing (or anything else solid 0.35-2 m tall) out along +z
        // from zFrom, alongside a flight running from x = xa to xb, looking up to 3 m out. Taken
        // at eight points along the flight; the middle value, so a single post or lamp by the
        // railing doesn't cut the flight short. Nothing found: fallback.
        private static float RailingAlong(Transform g, float xa, float xb, float zFrom, float groundY, float fallback)
        {
            var found = new List<float>();
            for (int i = 1; i <= 8; i++)
            {
                float x = Mathf.Lerp(xa, xb, i / 8f);
                for (float z = zFrom; z <= zFrom + 3f; z += 0.05f)
                {
                    var from = g.TransformPoint(new Vector3(x, 4f, z));
                    bool hitHere = false;
                    foreach (var hit in Physics.RaycastAll(from, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider == null || hit.collider.transform.IsChildOf(_root.transform) || IsBin(hit.collider.transform)) continue;
                        float h = g.InverseTransformPoint(hit.point).y - groundY;
                        if (h > 0.35f && h < 2f) { hitHere = true; break; }
                    }
                    if (hitHere) { found.Add(z); break; }
                }
            }
            if (found.Count < 3) return fallback;
            found.Sort();
            return found[found.Count / 2];
        }

        // How high the ground is at the foot of a fan ramp going one way (walkway-local, as the
        // height the ramp comes down to), or null if that way isn't clear: over water, ending on
        // the lip of a drop, or something standing up through the ramp on the way.
        private static float? FanSpoke(Transform g, float cx, float cz, float sx, float deg, float r, float topY, float landY, float sea, float edgeY)
        {
            float cos = Mathf.Cos(deg * Mathf.Deg2Rad), sin = Mathf.Sin(deg * Mathf.Deg2Rad);
            float? GroundAt(float rad)
            {
                var p = g.TransformPoint(new Vector3(cx + sx * cos * rad, 0f, cz - sin * rad));
                var h = Siting.GroundHeight(p.x, p.z, _root.transform);
                return h.HasValue && h.Value > sea + 0.3f ? h.Value - edgeY : (float?)null;
            }
            var foot = GroundAt(r);
            if (!foot.HasValue || !GroundAt(r + 0.5f).HasValue) return null;
            float end = foot.Value + landY;
            if (end > topY - 0.4f || end < topY - 3f) return null;
            for (float t = 0.15f; t < 1f; t += 0.1f)
            {
                var h = GroundAt(r * t);
                if (!h.HasValue || h.Value > Mathf.Lerp(topY, end, t) + 0.1f) return null;
            }
            return end;
        }

        // Height (above the quay edge) of any railing or fence where a walkway meets the land:
        // the top of anything solid under 2 m tall in the walkway's path near the edge.
        // (atZ: how far along the walkway it stands - negative is inland of the quay edge.)
        private static float RailingTop(Transform g) => RailingTop(g, out _);
        private static float RailingTop(Transform g, out float atZ)
        {
            float best = 0f;
            atZ = 0f;
            for (float z = -2.5f; z <= 0.8f; z += 0.2f)
            for (float x = -1.2f; x <= 1.21f; x += 0.4f)
            {
                var from = g.TransformPoint(new Vector3(x, 4f, z));
                foreach (var hit in Physics.RaycastAll(from, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.transform.IsChildOf(_root.transform)) continue;
                    float h = g.InverseTransformPoint(hit.point).y;
                    if (h > best && h < 2f) { best = h; atZ = z; }
                }
            }
            return best;
        }

        // ================================================================ upper floor

        // Second storey: a glass floor ring at 4.2 m, open in the middle round the weed plant
        // (with a glass balustrade), on a steel frame and white columns, reached by a wooden
        // staircase along the inside of the dome wall that comes up through an opening in the ring.
        public const float UpperFloorY = GhFloor + 4.05f;
        // Ring floor radii, and the stair opening as dome angles (0 = far side from the door). The
        // opening starts about a third of the way up (over 3 m clear under the floor there, so you
        // can come down it without the floor edge in your face) and runs to the top; the bottom of
        // the flight stays under the glass floor. (On the floor's 3-degree grid.)
        public const float UpperRIn = 4.5f, UpperRHoleIn = 8.1f, UpperRHoleOut = 11.05f;
        public const float UpperHoleFrom = 258f, UpperHoleTo = 289f;

        // Buildable tiles. Ground floor: the ring between the plant's planter and the dome wall.
        public static bool GroundTileAllowed(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + z * z);
            return r >= BuildInnerRadius && r <= BuildOuterRadius;
        }

        // Outside deck: no building out there - only a 2 x 2 m patch for the cleaners' trash can
        // (the game's cleaners only collect rubbish around a placed trash can). It's right of the
        // entrance, clear of where employees wait (left of the door), the walkway arrival point
        // and the for-sale sign.
        public static readonly Vector2 TrashCanSpot = new Vector2(8f, -13.5f);
        public static bool DeckTileAllowed(float x, float z) =>
            Mathf.Abs(x - TrashCanSpot.x) <= 1f && Mathf.Abs(z - TrashCanSpot.y) <= 1f;

        // Upstairs: the glass ring inside the railing, out to where there is still standing room
        // under the dome, leaving out the stair opening and the landing at its top.
        public static bool UpperTileAllowed(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + z * z);
            if (r < UpperRIn + 0.7f || r > 10.7f) return false;
            float a = Mathf.Atan2(x, z) * Mathf.Rad2Deg;
            if (a < 0f) a += 360f;
            return !(a > UpperHoleFrom - 4f && a < UpperHoleTo + 5f && r > UpperRHoleIn - 0.5f);
        }

        private static void BuildUpperFloor(Transform g)
        {
            var f = Kit.Group(g, "UpperFloor", Vector3.zero).transform;
            const float y = UpperFloorY;
            const float rIn = UpperRIn, rHoleIn = UpperRHoleIn, rHoleOut = UpperRHoleOut, rOut = 11.4f;
            const float holeFrom = UpperHoleFrom, holeTo = UpperHoleTo;
            const int segs = 120;
            const float step = 360f / segs;
            bool InHole(float a) => a > holeFrom && a < holeTo;
            Vector3 At(float angleDeg, float r, float h) => new Vector3(Mathf.Sin(angleDeg * Mathf.Deg2Rad) * r, h, Mathf.Cos(angleDeg * Mathf.Deg2Rad) * r);

            // Glass floor (one mesh; the collider uses the same up-facing mesh).
            var verts = new List<Vector3>();
            var tris = new List<int>();
            void Quad(float a0, float a1, float r0, float r1)
            {
                int s = verts.Count;
                verts.Add(At(a0, r0, y)); verts.Add(At(a1, r0, y)); verts.Add(At(a0, r1, y)); verts.Add(At(a1, r1, y));
                var n = Vector3.Cross(verts[s + 2] - verts[s], verts[s + 1] - verts[s]);
                if (n.y >= 0f) { tris.Add(s); tris.Add(s + 2); tris.Add(s + 1); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s + 3); }
                else { tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s + 1); tris.Add(s + 3); tris.Add(s + 2); }
            }
            for (int i = 0; i < segs; i++)
            {
                float a0 = i * step, a1 = a0 + step, mid = a0 + step * 0.5f;
                Quad(a0, a1, rIn, rHoleIn);
                if (!InHole(mid)) Quad(a0, a1, rHoleIn, rHoleOut);
                Quad(a0, a1, rHoleOut, rOut);
            }
            var floorMesh = new Mesh { name = "UpperFloorGlass" };
            floorMesh.vertices = verts.ToArray();
            floorMesh.triangles = tris.ToArray();
            floorMesh.RecalculateNormals();
            floorMesh.RecalculateBounds();
            Kit.MeshObject(f, "GlassFloor", floorMesh, new[] { Kit.Glass("floorglass", new Color(0.82f, 0.94f, 0.96f, 0.3f)) }, true);

            // Steel frame under it: rings, and radial beams (none across the stair opening).
            var steel = Kit.Mat("steel", new Color(0.85f, 0.87f, 0.88f), 0.5f, null, null, 0.4f);
            void Beam(Vector3 a, Vector3 b, float t) =>
                Kit.Box(f, "Beam", (a + b) * 0.5f, new Vector3(t, t, Vector3.Distance(a, b) + 0.02f), steel, false, Quaternion.LookRotation(b - a).eulerAngles);
            foreach (float r in new[] { rIn, rHoleIn, rHoleOut, rOut - 0.05f })
                for (int i = 0; i < segs; i += 2)
                {
                    float a0 = i * step, a1 = a0 + step * 2f;
                    Beam(At(a0, r, y - 0.07f), At(a1, r, y - 0.07f), 0.12f);
                }
            for (float a = 0f; a < 360f; a += 15f)
            {
                if (InHole(a) || InHole(a + 1f) || InHole(a - 1f)) continue;
                Beam(At(a, rIn, y - 0.09f), At(a, rOut - 0.05f, y - 0.09f), 0.1f);
            }

            // White columns down to the ground floor (kept clear of the door and the stairs).
            foreach (float r in new[] { rIn + 0.15f, rHoleOut + 0.2f })
                for (float a = 15f; a < 360f; a += 30f)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22f || (a > 223f && a < 299f)) continue;
                    Kit.Post(f, "Column", At(a, r, GhFloor), 0.09f, y - GhFloor - 0.1f, mFrame);
                }

            // Glass balustrade round the opening over the plant.
            var railGlass = Kit.Glass("railglass", new Color(0.85f, 0.95f, 0.97f, 0.28f));
            void Railing(Vector3 a, Vector3 b)
            {
                var mid = (a + b) * 0.5f;
                float len = Vector3.Distance(a, b) + 0.02f;
                var rot = Quaternion.LookRotation(b - a).eulerAngles;
                Kit.Box(f, "RailGlass", mid + Vector3.up * 0.5f, new Vector3(0.03f, 0.95f, len), railGlass, true, rot);
                Kit.Box(f, "Handrail", mid + Vector3.up * 1.02f, new Vector3(0.07f, 0.06f, len), steel, false, rot);
            }
            for (int i = 0; i < segs; i += 2)
                Railing(At(i * step, rIn + 0.04f, y), At((i + 2) * step, rIn + 0.04f, y));

            // Railing round the stair opening (the top end, where the stairs arrive, stays open).
            for (float a = holeFrom; a < holeTo - 0.01f; a += step)
            {
                Railing(At(a, rHoleIn, y), At(Mathf.Min(a + step, holeTo), rHoleIn, y));
                Railing(At(a, rHoleOut, y), At(Mathf.Min(a + step, holeTo), rHoleOut, y));
            }
            Railing(At(holeFrom, rHoleIn, y), At(holeFrom, rHoleOut, y));

            // The staircase: straight flight along the wall, rising towards the opening's open end,
            // with a short landing onto the glass floor.
            const float stairX = -9.6f, zBottom = -2.9f, zTop = 2.9f;
            var planks = Kit.Mat("walkplanks", Color.white, 0.1f, Kit.PlankTex(new Color(0.66f, 0.5f, 0.33f)), Vector2.one);
            var path = new List<Vector3>
            {
                new Vector3(stairX, GhFloor + 0.02f, zBottom - 0.6f),
                new Vector3(stairX, GhFloor + 0.02f, zBottom),
                new Vector3(stairX, y + 0.01f, zTop),
                new Vector3(stairX, y + 0.01f, zTop + 0.9f),
            };
            Kit.MeshObject(f, "Stairs", Kit.Ribbon(path, 1.25f, 0.18f, 1.6f), new[] { planks }, true);
            float rise = y - GhFloor;
            int steps = Mathf.RoundToInt(rise / 0.2f);
            for (int s = 0; s < steps; s++)
            {
                float k = (s + 0.5f) / steps;
                Kit.Box(f, "Tread", new Vector3(stairX, GhFloor + rise * k + 0.04f, Mathf.Lerp(zBottom, zTop, k)), new Vector3(2.48f, 0.05f, 0.2f), mDarkWood, false);
            }
            foreach (float side in new[] { -1.22f, 1.22f })
            {
                var a = new Vector3(stairX + side, GhFloor + 1f, zBottom);
                var b = new Vector3(stairX + side, y + 1f, zTop);
                Kit.Box(f, "StairRail", (a + b) * 0.5f, new Vector3(0.06f, 0.06f, Vector3.Distance(a, b)), steel, true, Quaternion.LookRotation(b - a).eulerAngles);
                for (float k = 0f; k <= 1.001f; k += 0.2f)
                {
                    var p = Vector3.Lerp(a, b, k);
                    Kit.Post(f, "StairPost", new Vector3(p.x, p.y - 1f, p.z), 0.025f, 1f, steel, false);
                }
            }
            Kit.Post(f, "StairColumn", new Vector3(stairX, GhFloor, 0f), 0.07f, rise * 0.5f - 0.2f, mFrame);
        }

        // ================================================================ greenhouse dome
        private static void BuildGreenhouse()
        {
            var g = Kit.Group(_island, "Greenhouse", Vector3.zero).transform;
            const float R = DomeRadius;
            const int cols = 24, bands = 8;
            float colStep = 360f / cols;
            float baseY = GhFloor + DomeWall;

            // Round slab and plank floor.
            // (Slab top sits just under the plank floor so the two never flicker through each other.)
            Kit.Post(g, "Slab", new Vector3(0f, GhFloor - 1.43f, 0f), R + 0.6f, 1.4f, mConcrete);
            var deck = Kit.Post(g, "Floor", new Vector3(0f, GhFloor - 0.03f, 0f), R - 0.1f, 0.04f, mPlanks);
            deck.GetComponent<MeshRenderer>().sharedMaterial = Kit.Mat("domefloor", Color.white, 0.1f, Kit.PlankTex(new Color(0.62f, 0.46f, 0.3f)), new Vector2(6f, 6f));

            // Points on the dome: column c edge at azimuth (c - 0.5) * step, so column 12 is
            // centred on 180 deg (the door); band b edge at elevation b * 90/bands.
            Vector3 P(int c, int b)
            {
                float az = ((c - 0.5f) * colStep) * Mathf.Deg2Rad;
                float el = (b * 90f / bands) * Mathf.Deg2Rad;
                float r = R * Mathf.Cos(el);
                return new Vector3(Mathf.Sin(az) * r, baseY + R * Mathf.Sin(el), Mathf.Cos(az) * r);
            }
            // The main door faces the nearest walkway (column 12, at 180 deg - the island is turned
            // that way); every other walkway gets a door of its own in the column facing it.
            const int doorCol = cols / 2;
            var doorCols = new List<int> { doorCol };
            foreach (var d in _site.Docks)
            {
                var w = d - _site.Center; w.y = 0f;
                if (w.sqrMagnitude < 1f) continue;
                var l = _island.InverseTransformDirection(w.normalized);
                float az = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
                if (az < 0f) az += 360f;
                int col = Mathf.RoundToInt(az / colStep) % cols;
                // Not next to the main door (its wall holds the save intercom), nor where the
                // stairs to the upper floor run inside (about 243..289 degrees, bottom to top).
                if (doorCols.Contains(col) || Mathf.Abs(col - doorCol) <= 1) continue;
                if (col * colStep > 243f - colStep && col * colStep < UpperHoleTo + colStep)
                {
                    Core.Log.Warning($"A walkway faces the stairs inside the dome - no second door on that side.");
                    continue;
                }
                doorCols.Add(col);
            }
            bool IsDoor(int c, int b) => doorCols.Contains(c) && b == 0;

            // Timber base wall, one straight segment per column, open at the doors.
            for (int c = 0; c < cols; c++)
            {
                if (doorCols.Contains(c)) continue;
                var a = P(c, 0); var b = P(c + 1, 0);
                a.y = b.y = GhFloor + DomeWall * 0.5f;
                Kit.Box(g, "BaseWall", (a + b) * 0.5f, new Vector3(0.3f, DomeWall, Vector3.Distance(a, b) + 0.05f), mDarkWood, true,
                        Quaternion.LookRotation(b - a).eulerAngles);
            }

            // Glass panels: one mesh (two triangles per panel), with a collider so it's solid.
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int c = 0; c < cols; c++)
            for (int b = 0; b < bands; b++)
            {
                if (IsDoor(c, b)) continue;
                int s = verts.Count;
                verts.Add(P(c, b)); verts.Add(P(c + 1, b)); verts.Add(P(c, b + 1)); verts.Add(P(c + 1, b + 1));
                tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);
                tris.Add(s + 1); tris.Add(s + 2); tris.Add(s + 3);
            }
            var glass = new Mesh { name = "DomeGlass" };
            glass.vertices = verts.ToArray();
            glass.triangles = tris.ToArray();
            glass.RecalculateNormals();
            glass.RecalculateBounds();
            Kit.MeshObject(g, "DomeGlass", glass, new[] { mGlass }, false);

            // Solid from both sides: a collision copy of the glass with every panel facing both
            // ways (a one-sided mesh collider let you slip through from the back), plus an
            // invisible wall round the lower part of the dome everywhere except the doorway.
            var solidTris = new List<int>(tris);
            for (int i = 0; i < tris.Count; i += 3) { solidTris.Add(tris[i]); solidTris.Add(tris[i + 2]); solidTris.Add(tris[i + 1]); }
            var solid = new Mesh { name = "DomeGlassCollision" };
            solid.vertices = verts.ToArray();
            solid.triangles = solidTris.ToArray();
            solid.RecalculateBounds();
            var solidGo = new GameObject("DomeGlassCollision");
            solidGo.transform.SetParent(g, false);
            solidGo.AddComponent<MeshCollider>().sharedMesh = solid;
            for (int c = 0; c < cols; c++)
            {
                if (doorCols.Contains(c)) continue;
                var a = P(c, 0); var b = P(c + 1, 0);
                float top = P(c, 1).y + 0.3f;
                a.y = b.y = (GhFloor + top) * 0.5f;
                var wall = new GameObject("GlassBlocker");
                wall.transform.SetParent(g, false);
                wall.transform.localPosition = (a + b) * 0.5f;
                wall.transform.localRotation = Quaternion.LookRotation(b - a);
                wall.AddComponent<BoxCollider>().size = new Vector3(0.35f, top - GhFloor, Vector3.Distance(a, b) + 0.1f);
            }

            // Frame: meridian ribs and rings along every panel edge.
            void Rib(Vector3 a, Vector3 b, float thick = 0.09f) =>
                Kit.Box(g, "Rib", (a + b) * 0.5f, new Vector3(thick, thick, Vector3.Distance(a, b) + 0.06f), mFrame, false,
                        Quaternion.LookRotation(b - a).eulerAngles);
            for (int c = 0; c < cols; c++)
            for (int b = 0; b < bands; b++)
            {
                Rib(P(c, b), P(c, b + 1));
                if (!IsDoor(c, b)) Rib(P(c, b), P(c + 1, b), b == 0 ? 0.14f : 0.09f);
            }
            Kit.Ball(g, "Cap", new Vector3(0f, baseY + R, 0f), new Vector3(0.8f, 0.3f, 0.8f), mFrame);

            // Each doorway: frame posts either side and a pair of sliding glass doors, locked shut
            // until you own the property, then opening by themselves for you and your employees.
            // (Their layer keeps them out of the navmesh bake, so employees still path through.)
            var doorGlass = Kit.Glass("doorglass", new Color(0.82f, 0.95f, 0.95f, 0.35f));
            int noNav = LayerMask.NameToLayer("Ignore Raycast");
            _doors.Clear();
            for (int di = 0; di < doorCols.Count; di++)
            {
                int dc = doorCols[di];
                float doorTop = P(dc, 1).y;
                foreach (int c in new[] { dc, dc + 1 })
                {
                    var p = P(c, 0);
                    Kit.Box(g, "DoorPost", new Vector3(p.x, (GhFloor + doorTop) * 0.5f, p.z), new Vector3(0.18f, doorTop - GhFloor, 0.18f), mFrame, false);
                }

                var jL = P(dc, 0); var jR = P(dc + 1, 0);
                jL.y = jR.y = GhFloor;
                var across = jR - jL;
                float doorW = across.magnitude;
                across /= doorW;
                var inward = -((jL + jR) * 0.5f); inward.y = 0f; inward.Normalize();
                float doorH = doorTop - GhFloor - 0.05f;
                var doorMid = (jL + jR) * 0.5f;
                var door = new Door { Name = di == 0 ? "Door" : $"Door {di + 1}", OutDir = -inward };
                for (int s = -1; s <= 1; s += 2)
                {
                    // Open: slid sideways along the outside of the wall (the wall bends inwards past
                    // the posts, so outside there's room), leaving the inside wall clear for the intercom.
                    var closed = doorMid + across * (s * doorW * 0.25f);
                    var open = doorMid + across * (s * doorW * 0.64f) - inward * 0.28f;
                    var panel = Kit.Group(g, "DoorPanel", closed).transform;
                    panel.localRotation = Quaternion.LookRotation(across);
                    Kit.Box(panel, "Glass", new Vector3(0f, doorH * 0.5f, 0f), new Vector3(0.05f, doorH, doorW * 0.5f), doorGlass, true);
                    Kit.Box(panel, "FrameTop", new Vector3(0f, doorH - 0.04f, 0f), new Vector3(0.08f, 0.08f, doorW * 0.5f), mFrame, false);
                    Kit.Box(panel, "FrameBottom", new Vector3(0f, 0.04f, 0f), new Vector3(0.08f, 0.08f, doorW * 0.5f), mFrame, false);
                    Kit.Box(panel, "FrameEdge", new Vector3(0f, doorH * 0.5f, -s * doorW * 0.25f), new Vector3(0.08f, doorH, 0.06f), mFrame, false);
                    Kit.Box(panel, "Handle", new Vector3(0f, 1.05f, -s * (doorW * 0.25f - 0.15f)), new Vector3(0.12f, 0.35f, 0.04f), mDark, false);
                    if (noNav >= 0) foreach (var tr in panel.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = noNav;
                    door.Panels.Add(panel);
                    door.Closed.Add(closed);
                    door.Open.Add(open);
                }
                door.Anchor = Kit.Group(g, "DoorSensor", doorMid + Vector3.up * 1f).transform;
                _doors.Add(door);
            }
            if (_doors.Count > 1) Core.Log.Msg($"The dome has {_doors.Count} doors, one facing each walkway.");

            // Save point on the inside face of the base wall right beside the door, like the
            // intercom by the door at other properties. It only works once the place is yours.
            var w0 = P(doorCol + 1, 0); var w1 = P(doorCol + 2, 0);
            w0.y = w1.y = GhFloor;
            var along = (w1 - w0).normalized;
            var wallIn = Vector3.Cross(Vector3.up, along);
            if (Vector3.Dot(wallIn, -(w0 + w1)) < 0f) wallIn = -wallIn;   // point into the dome
            // Base wall is 0.3 m thick, centred on the w0-w1 line: its inside face is 0.15 m in.
            // 0.35 m from the door post (right beside the door), 0.9 m up (the wall is DomeWall = 1.2 m high).
            // The water tap goes on the other side of the doorway: the same kind of spot on the
            // inside of the base wall to the left of the door (as you face out), floor level.
            {
                var l0 = P(doorCol, 0); var l1 = P(doorCol - 1, 0);
                l0.y = l1.y = GhFloor;
                var alongL = (l1 - l0).normalized;
                var inL = Vector3.Cross(Vector3.up, alongL);
                if (Vector3.Dot(inL, -(l0 + l1)) < 0f) inL = -inL;
                TapWallPoint = g.TransformPoint(l0 + alongL * 0.6f + inL * 0.151f);
                TapWallNormal = g.TransformDirection(inL);
                TapWallSet = true;
            }
            var mount = Kit.Group(g, "SaveMount", w0 + along * 0.35f + wallIn * 0.151f + Vector3.up * (GhFloor + 0.9f - w0.y)).transform;
            mount.localRotation = Quaternion.LookRotation(wallIn);
            _savePoint = AddSavePoint(mount, Vector3.zero, mount.forward);
            // Check: straight behind the intercom should be the timber wall, a few mm away.
            Physics.SyncTransforms();
            var probe = mount.position + mount.forward * 0.1f;
            var behind = Physics.RaycastAll(probe, -mount.forward, 0.6f, ~0, QueryTriggerInteraction.Ignore);
            string wallHit = "nothing";
            float best = 99f;
            foreach (var h in behind)
                if (h.collider != null && h.collider.name == "BaseWall" && h.distance < best) { best = h.distance; wallHit = $"{h.collider.name} {h.distance - 0.1f:0.000} m behind"; }
            Core.Log.Msg($"Save intercom on the wall: {wallHit}, {mount.position.y - _island.position.y - GhFloor:0.00} m above the floor.");

            // (No sign over the door; this point is still used for the porch light.)
            var front = (P(doorCol, 1) + P(doorCol + 1, 1)) * 0.5f;

            BuildWeedPlant(g);
            BuildUpperFloor(g);

            // Lights: soft lamps hung round the dome (always on so you can build at night)
            // plus a porch light over the door.
            for (int i = 0; i < 4; i++)
            {
                float a = (i * 90f + 45f) * Mathf.Deg2Rad;
                var p = new Vector3(Mathf.Sin(a) * 6.5f, baseY + 6.5f, Mathf.Cos(a) * 6.5f);
                // Hang from the glass right above: the dome is only sqrt(R^2 - r^2) high out here.
                float glassAbove = Mathf.Sqrt(R * R - 6.5f * 6.5f) - 6.5f;
                Kit.Post(g, "Cord", p, 0.02f, glassAbove - 0.1f, mDark, false);
                Kit.Ball(g, "Lamp", p, new Vector3(0.6f, 0.4f, 0.6f), mLampGlow);
                Kit.AddLight(g, p - Vector3.up * 0.4f, new Color(1f, 0.95f, 0.85f), 13f, 2f);
            }
            _nightLights.Add(Kit.AddLight(g, front + new Vector3(0f, 0.2f, -1.2f), new Color(1f, 0.85f, 0.6f), 9f, 2f));


            // Outside, like the picture: white water tanks with a hose, and a wind turbine.
            // (Both kept clear of the walkways - see ChooseOutdoorSpots.)
            var tankMat = Kit.Mat("whitetank", new Color(0.92f, 0.92f, 0.9f), 0.35f);
            foreach (var p in TankSpots())
            {
                Kit.Post(g, "Tank", p, 0.55f, 1.8f, tankMat);
                Kit.Post(g, "TankLid", p + Vector3.up * 1.8f, 0.4f, 0.08f, mDark, false);
            }
            // A hose from the last tank into the dome.
            float hoseAz = _turbineAz + _tankSide * (5f + 2f * 4.7f);
            float hoseFrom = R, hoseTo = TurbineRadius - 0.9f - 2f * 0.45f - 0.55f;
            Kit.Box(g, "Hose", new Vector3(Mathf.Sin(hoseAz * Mathf.Deg2Rad), 0f, Mathf.Cos(hoseAz * Mathf.Deg2Rad)) * ((hoseFrom + hoseTo) * 0.5f) + Vector3.up * 0.1f,
                    new Vector3(0.1f, 0.1f, hoseTo - hoseFrom + 0.1f), Kit.Mat("hose", new Color(0.15f, 0.35f, 0.75f), 0.3f), false, new Vector3(0f, hoseAz, 0f));

            // Well onto the deck, halfway between the dome and the edge, facing out to sea (its
            // blades turn across the deck, never towards the dome).
            float ta = _turbineAz * Mathf.Deg2Rad;
            var turbine = Kit.Group(g, "WindTurbine", new Vector3(Mathf.Sin(ta), 0f, Mathf.Cos(ta)) * TurbineRadius, _turbineAz).transform;
            // Its rotor (on the -z side of the nacelle) faces the Docks: the shore where walkway
            // 2 comes in off the Docks road.
            if (_site.Docks.Count > 1)
            {
                var toDocks = _site.Docks[1] - turbine.position; toDocks.y = 0f;
                if (toDocks.sqrMagnitude > 1f) turbine.rotation = Quaternion.LookRotation(-toDocks.normalized);
            }
            Kit.Post(turbine, "Base", Vector3.zero, 0.6f, 0.4f, mConcrete);
            Kit.Post(turbine, "Mast", Vector3.zero, 0.12f, 11f, mFrame);
            Kit.Box(turbine, "Nacelle", new Vector3(0f, 11.1f, 0.2f), new Vector3(0.45f, 0.45f, 1.1f), mFrame, false);
            var hub = Kit.Group(turbine, "Hub", new Vector3(0f, 11.1f, -0.45f)).transform;
            _turbine = hub;
            Kit.Ball(hub, "Spinner", Vector3.zero, Vector3.one * 0.4f, mFrame);
            for (int i = 0; i < 3; i++)
            {
                var arm = Kit.Group(hub, "BladeArm", Vector3.zero).transform;
                arm.localRotation = Quaternion.Euler(0f, 0f, i * 120f);
                Kit.Box(arm, "Blade", new Vector3(0f, 1.8f, 0f), new Vector3(0.28f, 3.4f, 0.05f), mFrame, false, new Vector3(0f, 12f, 0f));
                // Lights along each blade (showing both sides), so the turning rotor is easy to
                // spot at night.
                foreach (float lightAt in new[] { 1.3f, 2.4f, 3.4f })
                    Kit.Ball(arm, "BladeLight", new Vector3(0f, lightAt, 0f), Vector3.one * 0.16f, WindmillLights.Bulb("blade", 5f), false);
            }
            // And a steady lamp in the middle of the blades, seen from far off. (Colour: E on the
            // mast - see WindmillLights.)
            var centre = hub.localPosition + new Vector3(0f, 0f, -0.3f);
            Kit.Ball(turbine, "CentreLamp", centre, Vector3.one * 0.34f, WindmillLights.Bulb("centre", 8f), false);
            WindmillLights.Lamp(turbine, centre + new Vector3(0f, 0f, -0.3f), 18f, 4f);
            WindmillLights.AddInteraction(turbine);
        }

        private static void MakeMaterials()
        {
            mSand = Kit.Mat("sand", Color.white, 0.05f, Kit.NoiseTex(new Color(0.86f, 0.78f, 0.58f), 0.12f, 6f, 3), Vector2.one);
            mJungleFloor = Kit.Mat("junglefloor", Color.white, 0.05f, Kit.NoiseTex(new Color(0.26f, 0.36f, 0.16f), 0.35f, 5f, 9), Vector2.one);
            mDirt = Kit.Mat("dirt", Color.white, 0.05f, Kit.NoiseTex(new Color(0.45f, 0.34f, 0.22f), 0.25f, 10f, 21), new Vector2(1f, 8f));
            mConcrete = Kit.Mat("concrete", Color.white, 0.1f, Kit.NoiseTex(new Color(0.68f, 0.66f, 0.62f), 0.08f, 8f, 5), new Vector2(4f, 4f));
            mGravel = Kit.Mat("gravel", Color.white, 0.05f, Kit.NoiseTex(new Color(0.62f, 0.6f, 0.55f), 0.35f, 40f, 27), new Vector2(3f, 3f));
            mAsphalt = Kit.Mat("asphalt", Color.white, 0.2f, Kit.NoiseTex(new Color(0.2f, 0.2f, 0.21f), 0.15f, 16f, 2), Vector2.one);
            mLine = Kit.Mat("yellowline", new Color(0.95f, 0.78f, 0.15f), 0.3f);
            mWhiteLine = Kit.Mat("whiteline", new Color(0.92f, 0.92f, 0.9f), 0.3f);
            mDarkWood = Kit.Mat("darkwood", new Color(0.3f, 0.2f, 0.12f), 0.1f);
            mPlanks = Kit.Mat("planks", Color.white, 0.1f, Kit.PlankTex(new Color(0.62f, 0.46f, 0.3f)), new Vector2(3f, 12f));
            mMetal = Kit.Mat("metal", new Color(0.55f, 0.58f, 0.6f), 0.4f, null, null, 0.4f);
            mFrame = Kit.Mat("ghframe", new Color(0.9f, 0.92f, 0.9f), 0.5f, null, null, 0.2f);
            mGlass = Kit.Glass("glass", new Color(0.75f, 0.9f, 0.88f, 0.22f));
            mWhite = Kit.Mat("white", new Color(0.93f, 0.93f, 0.9f), 0.25f);
            mRed = Kit.Mat("red", new Color(0.72f, 0.12f, 0.1f), 0.25f);
            mDark = Kit.Mat("dark", new Color(0.12f, 0.12f, 0.13f), 0.2f);
            mStone = Kit.Mat("stone", Color.white, 0.05f, Kit.NoiseTex(new Color(0.5f, 0.5f, 0.46f), 0.25f, 10f, 13), Vector2.one);
            mTrunk = Kit.Mat("trunk", Color.white, 0.05f, Kit.NoiseTex(new Color(0.48f, 0.36f, 0.24f), 0.3f, 30f, 17), new Vector2(1f, 3f));
            mBark = Kit.Mat("bark", Color.white, 0.05f, Kit.NoiseTex(new Color(0.38f, 0.32f, 0.25f), 0.35f, 24f, 19), new Vector2(1f, 4f));
            mLeafDark = Kit.Mat("leafdark", new Color(0.1f, 0.3f, 0.1f), 0.2f);
            mLeafMid = Kit.Mat("leafmid", new Color(0.16f, 0.42f, 0.13f), 0.2f);
            mLeafLight = Kit.Mat("leaflight", new Color(0.3f, 0.55f, 0.18f), 0.2f);
            mFrond = Kit.Mat("frond", new Color(0.22f, 0.5f, 0.17f), 0.1f);
            mFrond2 = Kit.Mat("frond2", new Color(0.3f, 0.58f, 0.2f), 0.1f);
            mBanana = Kit.Mat("banana", new Color(0.35f, 0.65f, 0.2f), 0.3f);
            mMoss = Kit.Mat("moss", new Color(0.2f, 0.38f, 0.14f), 0.05f);
            mVine = Kit.Mat("vine", new Color(0.18f, 0.35f, 0.12f), 0.1f);
            mLampGlow = Kit.Glow("lampglow", new Color(1f, 0.86f, 0.6f), 3f);
            mSignGreen = Kit.Mat("signgreen", new Color(0.05f, 0.38f, 0.2f), 0.4f);
            mSignPink = Kit.Mat("signpink", new Color(0.93f, 0.38f, 0.64f), 0.4f);
            mBlue = Kit.Mat("blue", new Color(0.15f, 0.35f, 0.7f), 0.3f);
            mTeal = Kit.Mat("teal", new Color(0.1f, 0.6f, 0.6f), 0.3f);
            mTank = Kit.Mat("tank", new Color(0.2f, 0.28f, 0.22f), 0.3f);
            mFlowerRed = Kit.Mat("flowerred", new Color(0.9f, 0.12f, 0.15f), 0.3f);
            mFlowerPink = Kit.Mat("flowerpink", new Color(0.95f, 0.4f, 0.7f), 0.3f);
            mFlowerYellow = Kit.Mat("floweryellow", new Color(1f, 0.85f, 0.2f), 0.3f);
            mFlowerOrange = Kit.Mat("flowerorange", new Color(1f, 0.5f, 0.1f), 0.3f);
        }

        private static void BuildOceanPatchIfNeeded()
        {
            // If the game's sea plane doesn't reach this far out, lay our own patch of it.
            var ocean = Siting.FindOcean();
            float reach = R + 60f;
            var c = _site.Center;
            if (ocean != null)
            {
                var b = ocean.bounds;
                bool covered = b.min.x < c.x - reach && b.max.x > c.x + reach && b.min.z < c.z - reach && b.max.z > c.z + reach;
                if (covered) return;
            }

            var mat = ocean != null ? ocean.sharedMaterial : Kit.Mat("sea", new Color(0.12f, 0.35f, 0.45f), 0.9f);
            float size = R * 2f + 400f;
            Kit.Box(_island, "SeaPatch", new Vector3(0f, SeaY - 0.05f, 0f),
                    new Vector3(size, 0.1f, size), mat, false);
            Core.Log.Msg("Sea plane didn't reach Greenhouse Island - added a local patch of sea.");
        }

        private static void StreetLamp(Transform parent, Vector3 basePos, float side, float height = 6.5f)
        {
            var lamp = Kit.Group(parent, "Lamp", basePos).transform;
            Kit.Post(lamp, "Pole", Vector3.zero, 0.1f, height, mMetal, false);
            Kit.Box(lamp, "Arm", new Vector3(-side * 0.8f, height - 0.1f, 0f), new Vector3(1.7f, 0.1f, 0.12f), mMetal, false);
            Kit.Box(lamp, "Head", new Vector3(-side * 1.6f, height - 0.2f, 0f), new Vector3(0.7f, 0.18f, 0.35f), mLampGlow, false);
            _nightLights.Add(Kit.AddLight(lamp, new Vector3(-side * 1.6f, height - 0.6f, 0f), new Color(1f, 0.85f, 0.62f), 20f, 3f));
        }

        // The save point by the door. Two parts:
        //  - a copy of the game's own "Intercom Save Point" object, so "E  Save game" works exactly
        //    as at other properties - its own visuals hidden, its interaction box moved onto the wall;
        //  - the visible intercom, built from the game's own intercom mesh and materials and set
        //    square on the wall: its face straight out into the dome, upright.
        // Which side of the model is its face is read off the game's original intercom: the side
        // up against a big solid surface (its wall) is the back.
        private static GameObject AddSavePoint(Transform parent, Vector3 localPos, Vector3 faceWorld)
        {
            try
            {
                SavePoint template = null;
                foreach (var sp in Resources.FindObjectsOfTypeAll<SavePoint>())
                {
                    if (sp == null || !sp.gameObject.scene.IsValid() || sp.IntObj == null || !sp.gameObject.activeInHierarchy) continue;
                    template = sp;
                    break;
                }
                if (template == null)
                {
                    Core.Log.Warning("No save point found to copy - the greenhouse won't have one.");
                    return null;
                }

                // The intercom body at the original: the biggest part under a metre across, looked
                // for on the save point and, failing that, on its parent.
                MeshRenderer body = null;
                foreach (var root in new[] { template.transform, template.transform.parent })
                {
                    if (root == null || body != null) continue;
                    foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var rf = r.GetComponent<MeshFilter>();
                        if (rf == null || rf.sharedMesh == null) continue;
                        float s = r.bounds.size.magnitude;
                        if (s < 1f && (body == null || s > body.bounds.size.magnitude)) body = r;
                    }
                }
                if (body == null)
                {
                    Core.Log.Warning("The game's save point has no intercom model to copy.");
                    return null;
                }
                // The game's level is static-batched, so the intercom's MeshFilter holds a whole
                // area's combined mesh; cut the intercom's own slice back out (see BatchedMesh).
                var extracted = BatchedMesh.Extract(body, out var how);
                Core.Log.Msg($"Save point: intercom '{body.name}' (static batched: {body.isPartOfStaticBatch}, {body.bounds.size.x:0.00} x {body.bounds.size.y:0.00} x {body.bounds.size.z:0.00} m) - extracted: {how}.");

                // Is there a big surface (a wall) just off the original intercom this way?
                bool Blocked(Vector3 c, Vector3 dirWorld)
                {
                    foreach (var hit in Physics.RaycastAll(c, dirWorld, 0.3f, ~0, QueryTriggerInteraction.Ignore))
                        if (hit.collider != null && hit.collider.bounds.size.magnitude > 0.8f) return true;
                    return false;
                }

                var model = new GameObject("Intercom");
                model.transform.SetParent(parent, false);
                model.transform.localPosition = localPos;
                float k = 1f;
                if (extracted != null)
                {
                    // The wall normal at the original = the horizontal direction the intercom is
                    // thinnest along.
                    var verts = extracted.vertices;
                    Vector3 axis = Vector3.forward; float thick = float.MaxValue;
                    for (int a = 0; a < 180; a++)
                    {
                        var d = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
                        float lo = float.MaxValue, hi = float.MinValue;
                        foreach (var v in verts) { float p = Vector3.Dot(v, d); if (p < lo) lo = p; if (p > hi) hi = p; }
                        if (hi - lo < thick) { thick = hi - lo; axis = d; }
                    }
                    // Its face points away from the wall; failing that, towards where you stand to
                    // use it (its interaction box sits out in front).
                    var c = body.bounds.center;
                    bool plus = Blocked(c, axis), minus = Blocked(c, -axis);
                    Vector3 face;
                    string why;
                    if (plus != minus) { face = plus ? -axis : axis; why = "wall behind it"; }
                    else
                    {
                        float lean = Vector3.Dot(template.IntObj.transform.position - c, axis);
                        if (Mathf.Abs(lean) > 0.005f) { face = lean > 0f ? axis : -axis; why = "its button box"; }
                        else { face = FaceFromNormals(extracted, axis) == axis ? axis : -axis; why = "its surface detail"; }
                    }

                    model.AddComponent<MeshFilter>().sharedMesh = extracted;
                    var imr = model.AddComponent<MeshRenderer>();
                    imr.sharedMaterials = body.sharedMaterials;
                    SetUpSaveFlash(imr, template);
                    // Same size as the original, turned so its face points into the dome.
                    model.transform.rotation = Quaternion.LookRotation(parent.forward, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(face, Vector3.up));
                    model.transform.position = parent.TransformPoint(localPos) + parent.forward * (thick * 0.5f + 0.003f);
                    Core.Log.Msg($"Save point: intercom is {thick * 100f:0.0} cm deep; face picked from {why}.");
                }
                else
                {
                    // Couldn't get the game's geometry: a plain white intercom of the same kind.
                    var white = Kit.Mat("intercomwhite", new Color(0.93f, 0.93f, 0.9f), 0.35f);
                    var grey = Kit.Mat("intercomgrey", new Color(0.35f, 0.36f, 0.38f), 0.3f);
                    Kit.Box(model.transform, "Body", new Vector3(0f, 0f, 0.02f), new Vector3(0.16f, 0.24f, 0.04f), white, false);
                    for (int i = 0; i < 5; i++)
                        Kit.Box(model.transform, "Grille", new Vector3(0f, 0.06f - i * 0.014f, 0.041f), new Vector3(0.09f, 0.005f, 0.004f), grey, false);
                    var button = Kit.Box(model.transform, "Button", new Vector3(0f, -0.06f, 0.043f), new Vector3(0.05f, 0.03f, 0.008f), white, false);
                    SetUpSaveFlash(button.GetComponent<MeshRenderer>(), template);
                    Core.Log.Warning("Couldn't cut the game's intercom out of its batched mesh - using a plain white one.");
                }
                var target = parent.TransformPoint(localPos + Vector3.forward * 0.05f);

                // The working save point: the game's own object, visuals hidden, box on the wall.
                // (If its interaction box sits on its parent rather than under it, copy the parent
                // too - as long as that's just the intercom, not a whole building - so the copy's
                // "Save game" prompt belongs to the copy and not to the original.)
                var saveSource = template.gameObject;
                var tp = template.transform.parent;
                if (!template.IntObj.transform.IsChildOf(template.transform) && tp != null && template.IntObj.transform.IsChildOf(tp))
                {
                    Bounds pb = default; bool pAny = false;
                    foreach (var c in tp.GetComponentsInChildren<Collider>(true)) { if (!pAny) { pb = c.bounds; pAny = true; } else pb.Encapsulate(c.bounds); }
                    if (!pAny || pb.size.magnitude < 2f) saveSource = tp.gameObject;
                }
                var copy = Object.Instantiate(saveSource, parent);
                copy.name = "SavePoint";
                copy.SetActive(true);
                foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                // Keep only the interaction box (anything else it brought could block the doorway
                // next to it), sized to the intercom.
                var copySp = copy.GetComponentInChildren<SavePoint>(true);
                var intGo = copySp != null && copySp.IntObj != null ? copySp.IntObj.gameObject : null;
                foreach (var col in copy.GetComponentsInChildren<Collider>(true))
                    if (intGo == null || col.gameObject != intGo) Object.DestroyImmediate(col);
                if (intGo != null)
                {
                    foreach (var col in intGo.GetComponents<Collider>()) Object.DestroyImmediate(col);
                    var hit = intGo.AddComponent<BoxCollider>();
                    var ls = intGo.transform.lossyScale;
                    hit.size = new Vector3(0.3f / Mathf.Max(Mathf.Abs(ls.x), 0.001f), 0.35f / Mathf.Max(Mathf.Abs(ls.y), 0.001f), 0.3f / Mathf.Max(Mathf.Abs(ls.z), 0.001f));
                    hit.center = Vector3.zero;
                }
                // Put the interaction box (its centre is its transform) right on the intercom.
                if (intGo != null) copy.transform.position += target - intGo.transform.position;
                else copy.transform.position = target;

                // One object to switch on and off together (active only once the place is owned).
                var both = new GameObject("SavePointGroup");
                both.transform.SetParent(parent, false);
                model.transform.SetParent(both.transform, true);
                copy.transform.SetParent(both.transform, true);
                var flat = target - _island.position; flat.y = 0f;
                Core.Log.Msg($"Greenhouse save point copied from '{template.name}' at {target} ({flat.magnitude:0.0} m from the dome centre).");
                return both;
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't add the greenhouse save point: " + e);
                return null;
            }
        }

        // ------------------------------------------------------------ save flash

        // When the game has saved, the white block on the intercom flashes green. The block is
        // the intercom model's part (material) named like a light / LED / button, or failing
        // that its brightest one; the flash is triggered by the game's SaveManager.onSaveComplete.
        private static MeshRenderer _intercomMr;
        private static int _intercomLight = -1;
        private static Material _flashGreen;
        private static bool _saveHooked;

        private static void SetUpSaveFlash(MeshRenderer mr, SavePoint template)
        {
            _intercomMr = mr;
            var mats = mr.sharedMaterials;
            var names = new List<string>();
            int best = -1; float bestScore = float.MinValue;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                var c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.grey;
                string n = m.name.ToLowerInvariant();
                float score = c.r + c.g + c.b;
                if (n.Contains("light") || n.Contains("led") || n.Contains("emiss") || n.Contains("glow") || n.Contains("button") || n.Contains("screen") || n.Contains("indicator")) score += 10f;
                names.Add($"{m.name} ({ColorUtility.ToHtmlStringRGB(c)})");
                if (score > bestScore) { bestScore = score; best = i; }
            }
            _intercomLight = best;
            _flashGreen ??= Kit.Glow("savedgreen", new Color(0.2f, 1f, 0.3f), 4f);
            Core.Log.Msg($"Save flash: intercom parts {string.Join(", ", names)} - '{(best >= 0 ? mats[best].name : "none")}' flashes green when the game is saved.");

            // What the game's own intercom does when saving (for reference).
            try
            {
                foreach (var (ev, label) in new[] { (template.onSaveStart, "start"), (template.onSaveComplete, "complete") })
                {
                    if (ev == null) continue;
                    for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                        Core.Log.Msg($"Save flash: the game's intercom on save {label} calls {ev.GetPersistentTarget(i)?.name}.{ev.GetPersistentMethodName(i)}");
                }
            }
            catch { }
            HookSaveFlash();
        }

        public static void HookSaveFlash()
        {
            if (_saveHooked) return;
            try
            {
                var sm = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.Persistence.SaveManager>.Instance;
                if (sm == null || sm.onSaveComplete == null) return;
                sm.onSaveComplete.AddListener((UnityEngine.Events.UnityAction)(System.Action)(() => MelonLoader.MelonCoroutines.Start(FlashSaved())));
                _saveHooked = true;
            }
            catch (System.Exception e) { Core.Log.Warning("Couldn't hook the intercom's save flash: " + e.Message); }
        }

        private static System.Collections.IEnumerator FlashSaved()
        {
            if (_intercomMr == null || _intercomLight < 0) yield break;
            var mats = _intercomMr.sharedMaterials;
            if (_intercomLight >= mats.Length) yield break;
            var original = mats[_intercomLight];
            for (int i = 0; i < 4; i++)
            {
                if (_intercomMr == null) yield break;
                mats[_intercomLight] = _flashGreen; _intercomMr.sharedMaterials = mats;
                yield return new WaitForSeconds(0.3f);
                if (_intercomMr == null) yield break;
                mats[_intercomLight] = original; _intercomMr.sharedMaterials = mats;
                yield return new WaitForSeconds(0.2f);
            }
        }

        // Nearest axis (+-x / +-y / +-z) to a direction.
        private static Vector3 Snap(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ax >= ay && ax >= az) return new Vector3(Mathf.Sign(v.x), 0f, 0f);
            if (ay >= az) return new Vector3(0f, Mathf.Sign(v.y), 0f);
            return new Vector3(0f, 0f, Mathf.Sign(v.z));
        }

        // Fallback when the original's wall can't be told apart: the detailed front of a model
        // (grille, screen, buttons) has more surface facing its way than the flat back does.
        private static Vector3 FaceFromNormals(Mesh mesh, Vector3 thin)
        {
            try
            {
                if (mesh.isReadable)
                {
                    float sum = 0f;
                    foreach (var n in mesh.normals) sum += Vector3.Dot(n, thin);
                    return sum >= 0f ? thin : -thin;
                }
            }
            catch { }
            return -thin;
        }

        private static void Palm(Transform parent, Vector3 basePos, float yaw, float scale, int seed)
        {
            var rng = new System.Random(seed);
            var p = Kit.Group(parent, "Palm", basePos, yaw).transform;
            p.localScale = Vector3.one * scale;

            Vector3 pos = Vector3.zero;
            Vector3 dir = Vector3.up;
            float lean = 3f + (float)rng.NextDouble() * 4f;
            const int segs = 8;
            const float segLen = 1.1f;
            for (int i = 0; i < segs; i++)
            {
                dir = Quaternion.Euler(lean, 0f, 0f) * dir;
                float radius = Mathf.Lerp(0.3f, 0.19f, (float)i / segs);
                var seg = Kit.Prim(PrimitiveType.Cylinder, p, "Trunk", pos + dir * (segLen * 0.5f),
                                   new Vector3(radius * 2f, segLen * 0.55f, radius * 2f), mTrunk, i < 2);
                seg.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir);
                pos += dir * segLen;
            }

            var crown = Kit.Group(p, "Crown", pos, 0f).transform;
            const int fronds = 8;
            for (int i = 0; i < fronds; i++)
            {
                float fy = i * 360f / fronds + (float)rng.NextDouble() * 15f;
                float droop = 18f + (float)rng.NextDouble() * 22f;
                var f = Kit.Group(crown, "FrondPivot", Vector3.zero).transform;
                f.localRotation = Quaternion.Euler(droop, fy, 0f);
                Kit.Ball(f, "Frond", new Vector3(0f, 0f, 1.9f), new Vector3(0.95f, 0.07f, 4.2f), i % 2 == 0 ? mFrond : mFrond2);
            }
            for (int i = 0; i < 3; i++)
            {
                float a = i * 120f * Mathf.Deg2Rad;
                Kit.Ball(crown, "Coconut", new Vector3(Mathf.Sin(a) * 0.3f, -0.3f, Mathf.Cos(a) * 0.3f), Vector3.one * 0.3f, mDarkWood);
            }
        }

        private static void AddMapMarker()
        {
            try
            {
                POI template = null;
                foreach (var poi in Resources.FindObjectsOfTypeAll<POI>())
                {
                    if (poi != null && poi.UIPrefab != null && poi.gameObject.scene.IsValid())
                    {
                        template = poi;
                        break;
                    }
                }
                if (template == null)
                {
                    Core.Log.Warning("No map marker template found - Greenhouse Island won't show on the map.");
                    return;
                }

                var go = new GameObject("PelicanKeyPOI");
                _marker = go;
                go.SetActive(false);
                go.transform.SetParent(_island, false);
                go.transform.localPosition = new Vector3(0f, 2f, 0f);
                var marker = go.AddComponent<POI>();
                marker.UIPrefab = template.UIPrefab;
                marker.MainTextVisibility = template.MainTextVisibility;
                marker.DefaultMainText = "Greenhouse Island";
                marker.AutoUpdatePosition = true;
                marker.Rotate = false;
                go.SetActive(true);
                marker.SetMainText("Greenhouse Island");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't add the map marker: " + e.Message);
            }
        }

        // The centrepiece: a big bushy cannabis plant, like an outdoor grow - a thick main stalk,
        // tiers of branches that get shorter towards the top (a Christmas-tree shape), serrated
        // fan leaves everywhere, and frosty buds with orange hairs on every branch tip and a fat
        // main cola on top. It stands in a black edging ring of soil and perlite.
        private static void BuildWeedPlant(Transform parent)
        {
            var t = Kit.Group(parent, "WeedPlant", new Vector3(0f, GhFloor, 0f)).transform;
            var rng = new System.Random(420);
            float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);

            var stemMat = Kit.Mat("weedstem", new Color(0.42f, 0.5f, 0.25f), 0.1f);
            var leafDark = Kit.Mat("weedleaf", new Color(0.08f, 0.28f, 0.08f), 0.3f);
            var leafLight = Kit.Mat("weedleaf2", new Color(0.12f, 0.36f, 0.1f), 0.3f);
            var budMat = Kit.Mat("weedbud", new Color(0.55f, 0.68f, 0.32f), 0.3f);
            var frost = Kit.Mat("weedfrost", new Color(0.88f, 0.92f, 0.82f), 0.6f);
            var hair = Kit.Mat("weedhair", new Color(0.9f, 0.5f, 0.15f), 0.2f);
            var edging = Kit.Mat("edging", new Color(0.06f, 0.06f, 0.06f), 0.3f);
            var soil = Kit.Mat("soil", new Color(0.2f, 0.14f, 0.09f), 0.05f);
            var perlite = Kit.Mat("perlite", new Color(0.93f, 0.93f, 0.9f), 0.2f);
            var leafMesh = FanLeafMesh();
            var veinMesh = FanLeafVeinMesh();
            var veinMat = Kit.Mat("weedvein", new Color(0.6f, 0.76f, 0.3f), 0.2f);

            // Black edging ring with soil and flecks of perlite.
            float ring = TreePlanterRadius - 0.9f;
            var planter = Kit.Group(t, "Planter", Vector3.zero).transform;
            Kit.Post(planter, "Edging", Vector3.zero, ring, 0.22f, edging);
            Kit.Post(planter, "Soil", new Vector3(0f, 0.2f, 0f), ring - 0.04f, 0.03f, soil, false);
            for (int i = 0; i < 90; i++)
            {
                float a = Rand(0f, Mathf.PI * 2f), r = Mathf.Sqrt((float)rng.NextDouble()) * (ring - 0.1f);
                Kit.Box(planter, "Perlite", new Vector3(Mathf.Sin(a) * r, 0.235f, Mathf.Cos(a) * r), Vector3.one * Rand(0.02f, 0.045f), perlite, false, new Vector3(0f, Rand(0f, 90f), 0f));
            }

            try { Kit.CombineByMaterial(planter); } catch { }

            // The game's own fully grown weed plant - its leaves and its buds - at tree size. Only
            // if that can't be found is the plant below built by hand instead.
            var gameBase = Kit.Group(t, "GamePlant", new Vector3(0f, 0.2f, 0f)).transform;
            // Three copies of it, turned a third of a turn apart and slightly offset, so the one
            // big plant is thick with leaves and buds all round.
            // As tall as the dome allows: its top is DomeWall + DomeRadius above the floor, less
            // the planter and a little headroom (x3 the old 6.5 m would stick 6 m out of the roof).
            const float plantHeight = DomeWall + DomeRadius - 0.2f - 0.6f;   // 12.4 m
            int grown = 0;
            for (int i = 0; i < 3; i++)
            {
                float yaw = i * 120f + 15f;
                var offset = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * (i == 0 ? 0f : 0.25f);
                var sub = Kit.Group(gameBase, "PlantCopy" + i, offset, yaw).transform;
                if (GameProps.CloneGrownWeedPlant(sub, plantHeight * (i == 0 ? 1f : 0.9f)) != null) grown++;
            }
            if (grown > 0)
            {
                var trunk = gameBase.gameObject.AddComponent<CapsuleCollider>();
                trunk.radius = 0.7f;
                trunk.height = 8f;
                trunk.center = Vector3.up * 4f;
                TreeColours.AddInteraction(gameBase, 3.2f);   // E on the trunk: colour menu
                _nightLights.Add(Kit.AddLight(t, new Vector3(0f, plantHeight * 0.6f, 0f), new Color(1f, 0.9f, 0.85f), 16f, 1.8f));
                return;
            }
            Core.Log.Warning("Couldn't find the game's weed plant - building one by hand instead.");

            // Main stalk: thick at the base, splitting into the canopy.
            const float height = 4.4f;
            Kit.Post(t, "Stalk", new Vector3(0f, 0.2f, 0f), 0.11f, 1.2f, stemMat);
            Kit.Post(t, "StalkUpper", new Vector3(0f, 1.35f, 0f), 0.075f, height - 1.35f, stemMat, false);

            void Leaf(Transform on, Vector3 pos, float yaw, float size, float droop)
            {
                var leaf = Kit.MeshObject(on, "FanLeaf", leafMesh, new[] { rng.NextDouble() < 0.5 ? leafDark : leafLight }, false);
                Kit.MeshObject(leaf.transform, "Veins", veinMesh, new[] { veinMat }, false);
                leaf.transform.localPosition = pos;
                leaf.transform.localRotation = Quaternion.Euler(droop, yaw, Rand(-12f, 12f));
                leaf.transform.localScale = Vector3.one * size;
            }

            void Bud(Transform on, Vector3 tip, float len, float thick)
            {
                // A cola: a stack of chunky calyx clusters, frosted, with orange hairs poking out.
                int lumps = Mathf.Max(3, Mathf.RoundToInt(len / 0.07f));
                for (int k = 0; k < lumps; k++)
                {
                    float f = (float)k / (lumps - 1);
                    float w = thick * (1f - f * 0.55f);
                    var c = tip + Vector3.up * (f * len) + new Vector3(Rand(-0.02f, 0.02f), 0f, Rand(-0.02f, 0.02f));
                    Kit.Ball(on, "Calyx", c, new Vector3(w, w * 1.15f, w), budMat, false, new Vector3(0f, Rand(0f, 360f), 0f));
                    Kit.Ball(on, "Trichomes", c + new Vector3(Rand(-w, w), Rand(0f, w), Rand(-w, w)) * 0.3f, Vector3.one * w * 0.55f, frost, false);
                    for (int h = 0; h < 3; h++)
                    {
                        var dir = Quaternion.Euler(Rand(-60f, 20f), Rand(0f, 360f), 0f) * Vector3.forward;
                        Kit.Box(on, "Pistil", c + dir * w * 0.45f, new Vector3(0.008f, 0.008f, w * 0.5f), hair, false, Quaternion.LookRotation(dir).eulerAngles);
                    }
                }
                // Little sugar leaves tucked in the bud.
                for (int s = 0; s < 3; s++)
                    Leaf(on, tip + Vector3.up * (len * Rand(0.1f, 0.6f)), Rand(0f, 360f), thick * 1.1f, Rand(-50f, -25f));
            }

            // Branches in pairs, each pair turned 90 degrees from the one below; long at the bottom,
            // short at the top.
            int node = 0;
            for (float y = 0.9f; y < height - 0.35f; y += 0.28f, node++)
            {
                float up = (y - 0.9f) / (height - 1.25f);
                float len = Mathf.Lerp(1.75f, 0.35f, Mathf.Pow(up, 0.85f)) * Rand(0.85f, 1.1f);
                for (int side = 0; side < 2; side++)
                {
                    float yaw = node * 90f + side * 180f + Rand(-20f, 20f);
                    var b = Kit.Group(t, "Branch", new Vector3(0f, y, 0f), yaw).transform;
                    // The branch rises as it goes out.
                    float rise = Rand(38f, 55f);
                    var dir = Quaternion.Euler(-rise, 0f, 0f) * Vector3.forward;
                    var stem = Kit.Prim(PrimitiveType.Cylinder, b, "Stem", dir * (len * 0.5f), new Vector3(0.04f, len * 0.5f, 0.04f), stemMat, false);
                    stem.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir);

                    // Fan leaves along it, big ones lower down.
                    int leaves = Mathf.Max(2, Mathf.RoundToInt(len / 0.35f));
                    for (int l = 0; l < leaves; l++)
                    {
                        float at = (l + 0.6f) / (leaves + 0.6f);
                        float size = Mathf.Lerp(0.55f, 0.32f, up) * Rand(0.8f, 1.15f);
                        Leaf(b, dir * (len * at), Rand(-70f, 70f), size, Rand(-10f, 25f));
                    }

                    Bud(b, dir * len, Mathf.Lerp(0.28f, 0.2f, up), Mathf.Lerp(0.11f, 0.08f, up));
                }
            }

            // Big fan leaves hanging off the lower stalk, and a crown of leaves round the top.
            for (int i = 0; i < 8; i++)
                Leaf(t, new Vector3(0f, Rand(0.9f, 1.8f), 0f), i * 45f + Rand(-10f, 10f), Rand(0.6f, 0.75f), Rand(5f, 25f));
            for (int i = 0; i < 6; i++)
                Leaf(t, new Vector3(0f, height - 0.2f, 0f), i * 60f, Rand(0.35f, 0.45f), Rand(-35f, -15f));

            // Main cola on top.
            Bud(t, new Vector3(0f, height - 0.35f, 0f), 0.6f, 0.16f);

            // Merge the hundreds of little pieces into a few meshes.
            try { Kit.CombineByMaterial(t); } catch (System.Exception e) { Core.Log.Warning("Merging the weed plant failed: " + e.Message); }  // (hand-built plant only)

            // A warm grow-light glow on it at night.
            _nightLights.Add(Kit.AddLight(t, new Vector3(0f, height + 0.8f, 0f), new Color(1f, 0.9f, 0.75f), 9f, 1.4f));
        }

        // A cannabis fan leaf like the real thing: seven long, narrow, saw-toothed leaflets fanning
        // from one point - the middle one longest, then a long pair, a medium pair, and a small pair
        // swept down and back - on a short stalk. Built as two meshes: the dark blade and the pale
        // centre vein on each leaflet. Flat, facing up, drawn on both sides; 1 m long along +z
        // with the stalk end as the pivot.
        private static Mesh _fanLeaf, _fanLeafVeins;
        private static Mesh FanLeafMesh()
        {
            if (_fanLeaf != null) return _fanLeaf;
            var angles = new[] { 0f, -30f, 30f, -60f, 60f, -102f, 102f };
            var lengths = new[] { 1f, 0.88f, 0.88f, 0.66f, 0.66f, 0.34f, 0.34f };
            var basePoint = new Vector3(0f, 0f, 0.3f);
            const int segs = 40;

            var bv = new List<Vector3>(); var bt = new List<int>();
            var vv = new List<Vector3>(); var vt = new List<int>();
            for (int l = 0; l < angles.Length; l++)
            {
                var rot = Quaternion.Euler(0f, angles[l], 0f);
                float len = lengths[l] * 0.7f, halfW = len * 0.075f;
                // Gentle upward fold along the midrib, and a slight droop towards the tip.
                Vector3 Along(float f, float side) => new Vector3(0f, 0.012f * Mathf.Abs(side) / Mathf.Max(halfW, 0.001f) - 0.03f * f * f * len, f * len);

                int start = bv.Count;
                bv.Add(basePoint);
                for (int s = 1; s <= segs; s++)
                {
                    float f = (float)s / segs;
                    // Widest a little below the middle, long pointed tip.
                    float w = halfW * Mathf.Sin(Mathf.PI * Mathf.Pow(f, 0.62f)) * (1f - 0.25f * f);
                    // Saw teeth: every other point sticks out and sits a bit further towards the tip.
                    bool tooth = s % 2 == 0 && f > 0.08f && f < 0.94f;
                    float fz = tooth ? Mathf.Min(1f, f + 0.35f / segs) : f;
                    if (tooth) w *= 1.35f; else w *= 0.9f;
                    bv.Add(basePoint + rot * (Along(fz, -w) + Vector3.left * w));
                    bv.Add(basePoint + rot * (Along(fz, w) + Vector3.right * w));
                }
                bv.Add(basePoint + rot * new Vector3(0f, -0.03f * len, len * 1.05f));
                int tip = bv.Count - 1;
                for (int s = 0; s < segs; s++)
                {
                    int la = s == 0 ? start : start + 1 + (s - 1) * 2, ra = s == 0 ? start : start + 2 + (s - 1) * 2;
                    int lb = start + 1 + s * 2, rb = start + 2 + s * 2;
                    bt.Add(la); bt.Add(lb); bt.Add(rb);
                    if (s > 0) { bt.Add(la); bt.Add(rb); bt.Add(ra); }
                }
                int lastL = start + 1 + (segs - 1) * 2;
                bt.Add(lastL); bt.Add(tip); bt.Add(lastL + 1);

                // Pale centre vein, a thin strip just proud of the blade.
                int v0 = vv.Count;
                const int vs = 12;
                for (int s = 0; s <= vs; s++)
                {
                    float f = (float)s / vs * 0.96f;
                    float w = 0.0045f * (1f - f * 0.6f);
                    var c = Along(f, 0f) + Vector3.up * 0.004f;
                    vv.Add(basePoint + rot * (c + Vector3.left * w));
                    vv.Add(basePoint + rot * (c + Vector3.right * w));
                }
                for (int s = 0; s < vs; s++)
                {
                    int a = v0 + s * 2;
                    vt.Add(a); vt.Add(a + 2); vt.Add(a + 1);
                    vt.Add(a + 1); vt.Add(a + 2); vt.Add(a + 3);
                }
            }

            // Stalk (part of the vein mesh, same pale green).
            int p = vv.Count;
            vv.Add(new Vector3(-0.01f, 0f, 0f)); vv.Add(new Vector3(0.01f, 0f, 0f));
            vv.Add(new Vector3(-0.008f, 0f, 0.31f)); vv.Add(new Vector3(0.008f, 0f, 0.31f));
            vt.Add(p); vt.Add(p + 2); vt.Add(p + 1); vt.Add(p + 1); vt.Add(p + 2); vt.Add(p + 3);

            _fanLeaf = DoubleSided(bv, bt, "FanLeaf");
            _fanLeafVeins = DoubleSided(vv, vt, "FanLeafVeins");
            return _fanLeaf;
        }

        private static Mesh FanLeafVeinMesh()
        {
            FanLeafMesh();
            return _fanLeafVeins;
        }

        // Adds a back face with its own vertices (so it gets its own flipped normals).
        private static Mesh DoubleSided(List<Vector3> verts, List<int> tris, string name)
        {
            int n = tris.Count, back = verts.Count;
            verts.AddRange(new List<Vector3>(verts));
            for (int i = 0; i < n; i += 3) { tris.Add(tris[i] + back); tris.Add(tris[i + 2] + back); tris.Add(tris[i + 1] + back); }
            var mesh = new Mesh { name = name };
            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

    }
}

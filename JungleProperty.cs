using System.Collections.Generic;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppFishNet.Managing.Object;
using Il2CppFishNet.Object;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Tiles;
using Il2CppTMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Greenhouse Island as a real, buyable property - listed by Ray like every other
    // property, price $0. Once owned, the greenhouse floor is its build grid and everything placed
    // there is saved by the game with the property, exactly like any other property's items.
    //
    // The game only knows about properties that exist in the scene, so this one is created at
    // runtime and spawned on the network (host / single player). If the save has data for it, it's
    // created the moment the game's property loader asks for it, so the vanilla loader restores
    // ownership and the placed items itself.
    internal static class JungleProperty
    {
        public const string Code = "junglegreenhouseisland";
        public const string Name = "Greenhouse Island";
        // What it used to be called: the game names the property's save file after the name.
        public const string OldName = "Jungle Greenhouse Island";
        // The property's object name (other mods, e.g. Wages Manager, find properties by it).
        public const string ObjectName = "JungleGreenhouseIslandProperty";
        // Fixed GUID for the greenhouse grid - placed items remember the grid they're on by this.
        public const string GridGuid = "4a1c7e52-9b3d-4f60-8e21-6d5b0c9f7a13";
        public const string UpperGridGuid = "4a1c7e52-9b3d-4f60-8e21-6d5b0c9f7a14";   // upstairs
        public const string DeckGridGuid = "4a1c7e52-9b3d-4f60-8e21-6d5b0c9f7a15";    // outside deck
        private const ushort NetworkCollectionId = 47_113;

        public static Property Property;
        private static GameObject _root;
        private static Transform _arrival;
        private static Transform _poster;

        public static bool IsOwned
        {
            get
            {
                try { return Property != null && Property.IsOwned; } catch { return false; }
            }
        }

        public static void Clear()
        {
            IslandNavMesh.Clear();
            Property = null;
            LoadedFromSave = false;
            _poster = null;
            _arrival = null;
            _root = null;
        }

        // Moves the property (bounds, sign, spawn points, map marker) along with the island.
        public static void Reposition()
        {
            var site = IslandBuilder.Current?.Site;
            if (_root == null || site == null) return;
            _root.transform.SetPositionAndRotation(site.Center, Quaternion.LookRotation(site.Dir));
            PlaceArrivalPoint(site);
            IslandNavMesh.Bake(site);
        }

        // New hires arrive at the platform's edge by the walkway
        // and walk up to the greenhouse; fired employees walk back there before they leave.
        private static void PlaceArrivalPoint(Site site)
        {
            if (_arrival == null) return;
            // Straight onto the deck by the boarding edge (no raycast: freshly built colliders
            // aren't in the physics scene until the next sync, which is how they ended up in the air).
            _arrival.position = IslandBuilder.IslandLandingPoint + Vector3.up * 0.05f;
            _arrival.rotation = Quaternion.LookRotation(site.Dir);
        }

        // Set once the save's island data has been loaded this session (see LoadPropertyPatch).
        public static bool LoadedFromSave;

        // Rename safeguard. The game names a property's save file after the property, so a save
        // made before the rename has "Jungle Greenhouse Island" and the next save adds
        // "Greenhouse Island" alongside it. Once both exist, the old one is stale: move it out of
        // the save into UserData/PelicanKey/old-save-files so it can never load again.
        public static void RetireOldSaveFile()
        {
            try
            {
                var lm = Il2CppScheduleOne.DevUtilities.PersistentSingleton<Il2CppScheduleOne.Persistence.LoadManager>.Instance;
                string save = lm != null ? lm.LoadedGameFolderPath : null;
                if (string.IsNullOrEmpty(save)) return;
                string folder = System.IO.Path.Combine(save, "Properties");
                if (!System.IO.Directory.Exists(folder)) return;

                var oldFiles = System.IO.Directory.GetFiles(folder, OldName + "*");
                if (oldFiles.Length == 0) return;
                if (System.IO.Directory.GetFiles(folder, Name + "*").Length == 0) return; // old file is still the only copy

                string backup = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PelicanKey", "old-save-files",
                    System.IO.Path.GetFileName(save.TrimEnd('\\', '/')) + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                System.IO.Directory.CreateDirectory(backup);
                foreach (var f in oldFiles)
                {
                    System.IO.File.Move(f, System.IO.Path.Combine(backup, System.IO.Path.GetFileName(f)));
                    Core.Log.Msg($"Moved the old-name save file '{System.IO.Path.GetFileName(f)}' to {backup}.");
                }
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't tidy up the old Jungle Greenhouse Island save file: " + e.Message);
            }
        }

        // Photographs the dome and puts the picture on our poster at Ray's.
        public static void MakeListingPhoto()
        {
            var site = IslandBuilder.Current?.Site;
            if (_poster == null || site == null) return;
            // Now the save has loaded (and with it which properties you own), move the poster
            // into a free slot on the board and write its details.
            try { GameProps.PlaceListingPoster(_poster, Property); }
            catch (System.Exception e) { Core.Log.Warning("Couldn't place the listing poster on the board: " + e.Message); }
            var photo = GameProps.PhotographDome(site);
            if (photo == null) return;
            if (GameProps.SetPosterPicture(_poster, photo))
                Core.Log.Msg("Listing poster at Ray's now shows a photo of the dome.");
            else
                Core.Log.Warning("Couldn't find where the picture goes on the listing poster.");
        }

        // Employees saved while stuck off the platform (e.g. in mid-air) get put back on the deck.
        public static void RescueStrandedEmployees()
        {
            var site = IslandBuilder.Current?.Site;
            if (Property == null || site == null || _arrival == null) return;
            int moved = 0;
            try
            {
                foreach (var emp in Property.Employees)
                {
                    if (emp == null) continue;
                    var p = emp.transform.position;
                    var flat = new Vector2(p.x - site.Center.x, p.z - site.Center.z);
                    bool onPlatform = flat.magnitude < site.Radius + 1f && Mathf.Abs(p.y - site.Center.y) < 2.5f;
                    if (onPlatform) continue;
                    Compat.Warp(emp.Movement, _arrival.position);
                    moved++;
                }
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't check on the island's employees: " + e.Message);
            }
            if (moved > 0) Core.Log.Msg($"Moved {moved} stranded employee(s) back onto the platform.");
        }

        // Once on load: each of the island's employees, whether they're paid and able to work,
        // and if not, why. The game only lets an employee work with a bed or locker assigned
        // (GetHome), paid for the day, and not at the end of the day. A short summary also pops up
        // on screen, because the usual reason a worker stands idle is something still to set up.
        public static void LogEmployeeStatus()
        {
            if (Property == null) return;
            try
            {
                Core.Log.Msg($"[Employees] {Property.Employees.Count} on Greenhouse Island (owned={Property.IsOwned}).");
                int noLocker = 0, unpaid = 0;
                foreach (var emp in Property.Employees)
                {
                    if (emp == null) continue;
                    bool home = emp.GetHome() != null;
                    if (!home) noLocker++;
                    else if (!emp.PaidForToday) unpaid++;
                    string why = !home ? "NO LOCKER/BED ASSIGNED - place one and assign it to them"
                               : !emp.PaidForToday ? "not paid today"
                               : emp.CanWork() ? "working" : "end of day";
                    Core.Log.Msg($"[Employees]   {((Object)emp).name}: paid={emp.PaidForToday}, locker={(home ? "yes" : "none")} -> {why}");
                }

                // Pots with no harvest destination: the botanist says "plant ready for harvest but
                // it has no destination" and stops.
                int pots = 0, noDestination = 0;
                foreach (var item in Property.BuildableItems)
                {
                    var pot = item != null ? item.TryCast<Il2CppScheduleOne.ObjectScripts.Pot>() : null;
                    if (pot == null) continue;
                    pots++;
                    var cfg = pot.Configuration != null ? pot.Configuration.TryCast<Il2CppScheduleOne.Management.PotConfiguration>() : null;
                    if (cfg != null && cfg.Destination != null && cfg.Destination.SelectedObject == null) noDestination++;
                }
                Core.Log.Msg($"[Employees] {pots} pot(s), {noDestination} with no harvest destination.");

                var problems = new List<string>();
                if (noLocker > 0) problems.Add($"{noLocker} employee{(noLocker == 1 ? "" : "s")} without a locker");
                if (unpaid > 0) problems.Add($"{unpaid} unpaid");
                if (noDestination > 0) problems.Add($"{noDestination} pot{(noDestination == 1 ? "" : "s")} with no harvest destination");
                if (problems.Count > 0) Hud.Toast("Greenhouse Island: " + string.Join(", ", problems) + " - they can't work until that's set up.", 12f);
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("[Employees] " + e.Message);
            }
        }

        private static bool ServerReady()
        {
            try
            {
                var sm = InstanceFinder.ServerManager;
                return sm != null && sm.OneServerStarted();
            }
            catch
            {
                return false;
            }
        }

        public static bool TryCreate()
        {
            if (Property != null) return true;
            if (!ServerReady()) return false;
            if (IslandBuilder.Current == null) Core.Rebuild();
            var site = IslandBuilder.Current?.Site;
            var space = IslandBuilder.GridSpace;
            if (site == null || space == null) return false;

            foreach (var p in Property.Properties)
                if (p != null && p.PropertyCode == Code) { Property = p; return true; }

            NetworkObject netObj = null;
            try
            {
                // Borrow the network manager and a map-marker prefab from an existing property.
                Property template = null;
                foreach (var p in Property.Properties)
                    if (p != null && p.PoI != null && p.PoI.UIPrefab != null && p.NetworkObject != null) { template = p; break; }
                if (template == null)
                {
                    Core.Log.Warning("No existing property to copy network/map settings from.");
                    return false;
                }
                var networkManager = template.NetworkObject.NetworkManager;

                var H = IslandBuilder.BuildHalfSize;
                float edge = IslandBuilder.PlatformRadius;

                _root = new GameObject(ObjectName);
                _root.SetActive(false);
                _root.transform.SetPositionAndRotation(site.Center, Quaternion.LookRotation(site.Dir));
                var t = _root.transform;

                netObj = _root.AddComponent<NetworkObject>();
                // The same initialise order as the game's own properties (FishNet logs "initialize order of 0
                // ... will cause issues" for a property left at the default).
                try { netObj._initializeOrder = template.NetworkObject._initializeOrder; } catch { }
                var prop = _root.AddComponent<Property>();

                prop.propertyName = Name;
                prop.propertyCode = Code;
                prop.Price = 0f;
                int capacity = Mathf.Clamp(Core.EmployeeCapacity.Value, 0, 30);
                prop.EmployeeCapacity = capacity;
                prop.OwnedByDefault = false;
                prop.IsOwnedVariable = string.Empty;
                // Normal culling, like every other property: with it off, the game still culls the
                // contents when you are more than 55 m away, but first un-culls them again - both
                // every physics tick - so all your pots, plants and lights were switched on and off
                // constantly whenever you left. Far enough that the island stays complete from the
                // Docks quay.
                prop.ContentCullingEnabled = true;
                prop.MinimumCullingDistance = 75f;
                prop.DefaultRotation = 0f;

                // Bounds: the whole platform - greenhouse and outside deck (items must be placed
                // inside a property's bounds).
                var bounds = Child(t, "BoundingBox", new Vector3(0f, 0.15f + H.y * 0.5f, 0f));
                var box = bounds.AddComponent<BoxCollider>();
                box.size = new Vector3(edge * 2f + 1f, H.y + 1f, edge * 2f + 1f);
                prop.BoundingBox = bounds;

                var container = Child(t, "Container", Vector3.zero);
                prop._Container_k__BackingField = container.AddComponent<PropertyContentsContainer>();

                var door = new Vector3(0f, 0.3f, -IslandBuilder.GreenhouseDepth * 0.5f - 3f);
                prop.SpawnPoint = Child(t, "SpawnPoint", door).transform;
                prop.InteriorSpawnPoint = Child(t, "InteriorSpawnPoint", new Vector3(0f, 0.3f, 0f)).transform;
                _arrival = Child(t, "NPCSpawnPoint", door).transform;
                prop.NPCSpawnPoint = _arrival;
                PlaceArrivalPoint(site);

                // Employees wait in a group on the deck beside the entrance until they have a
                // bed/locker; new hires step onto the deck at the front, right of the door.
                var idle = new Il2CppReferenceArray<Transform>(capacity);
                var spots = IslandBuilder.IdleSpots(capacity);
                for (int i = 0; i < capacity; i++)
                {
                    var p = Child(t, "IdlePoint" + i, spots[i]);
                    // Face out from the dome, towards the deck and the water, the way employees stand
                    // waiting outside other properties (the game turns them to the point's forward).
                    var outward = spots[i]; outward.y = 0f;
                    if (outward.sqrMagnitude > 0.01f) p.transform.localRotation = Quaternion.LookRotation(outward);
                    idle[i] = p.transform;
                }
                prop.EmployeeIdlePoints = idle;
                prop.DisposalArea = BuildDumpster(t, new Vector3(-11f, 0f, -11f));
                prop.LoadingDocks = BuildLoadingDocks(t, prop);
                prop.ObjectsToCull = new Il2CppReferenceArray<GameObject>(0);
                if (prop.Switches == null) prop.Switches = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Misc.ModularSwitch>();
                if (prop.Toggleables == null) prop.Toggleables = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Interaction.InteractableToggleable>();

                // Map marker (the game shows it with the "Owned" icon once bought).
                var poiGo = Child(t, "PoI", new Vector3(0f, 2f, 0f));
                var poi = poiGo.AddComponent<POI>();
                poi.UIPrefab = template.PoI.UIPrefab;
                poi.MainTextVisibility = template.PoI.MainTextVisibility;
                poi.DefaultMainText = Name;
                poi.AutoUpdatePosition = true;
                prop.PoI = poi;

                prop.ForSaleSign = BuildForSaleSign(t);
                // Our own poster on the "Properties for sale" board at Ray's (the game fills in the
                // title, price etc. on wake-up and takes it down once the place is bought).
                _poster = GameProps.MakeListingPoster(prop);
                prop.ListingPoster = _poster;

                // Awake (on activation) registers the property with the game and the save system.
                _root.SetActive(true);
                // The game has just written the name and price onto the sign - lay them out again.
                Kit.RelayoutAll(_root.transform);
                byte componentIndex = 0;
                netObj.UpdateNetworkBehaviours(null, ref componentIndex);
                prop.NetworkInitializeIfDisabled();

                var bucket = networkManager.GetPrefabObjects<SinglePrefabObjects>(NetworkCollectionId, true).Cast<SinglePrefabObjects>();
                bucket.AddObject(netObj, true);
                InstanceFinder.ServerManager.Spawn(netObj, null, _root.scene);

                if (!netObj.IsSpawned)
                    Core.Log.Warning("The island property didn't report as spawned - buying it may not work.");

                // A working copy of the game's water tap, behind the stairs in the dome.
                GreenhouseTap.Create(IslandBuilder.Island, tapObj =>
                {
                    bucket.AddObject(tapObj, true);
                    // No scene: passing one makes FishNet move the object into it first, which Unity
                    // refuses for anything that isn't top-level (the tap is inside the island) - and
                    // the spawn is abandoned. It's already in the right scene.
                    InstanceFinder.ServerManager.Spawn(tapObj, null, default(UnityEngine.SceneManagement.Scene));
                });

                Property = prop;

                // The greenhouse floor becomes this property's build grid.
                GreenhouseGrid.Create(prop, IslandBuilder.BuildHalfSize, GridGuid, IslandBuilder.GhFloor, IslandBuilder.GroundTileAllowed, GreenhouseGrid.Floor.Ground);
                GreenhouseGrid.Create(prop, IslandBuilder.BuildHalfSize, UpperGridGuid, IslandBuilder.UpperFloorY, IslandBuilder.UpperTileAllowed, GreenhouseGrid.Floor.Upper);
                // The outside deck too, so a trash can for the cleaners (or anything else) can stand out there.
                GreenhouseGrid.Create(prop, new Vector3(IslandBuilder.PlatformRadius, 1f, IslandBuilder.PlatformRadius), DeckGridGuid, 0f, IslandBuilder.DeckTileAllowed, GreenhouseGrid.Floor.Deck);

                // Walkable ground for employees (and anyone else) on the island.
                IslandNavMesh.Bake(site);

                Core.Log.Msg($"'{Name}' is on the market at Ray's Realty for $0." + (prop.IsOwned ? " (owned)" : ""));
                return true;
            }
            catch (System.Exception e)
            {
                Core.Log.Error("Couldn't create the island property: " + e);
                return false;
            }
        }

        private static GameObject Child(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go;
        }

        // Extra turn for the skip, degrees (negative = to the left, seen from above) - so its open
        // side is easy to walk up to.
        private const float DumpsterTurn = 65f;   // sloped open end towards the walkway side of the deck

        // Where cleaners take full trash bags: they stand at StandPoint and drop the bag in.
        private static PropertyDisposalArea BuildDumpster(Transform parent, Vector3 pos)
        {
            var d = Child(parent, "Dumpster", pos);
            var area = d.AddComponent<PropertyDisposalArea>();
            var stand = Child(d.transform, "StandPoint", Vector3.zero).transform;
            var drop = Child(d.transform, "TrashDropPoint", Vector3.zero).transform;
            area.StandPoint = stand;
            area.TrashDropPoint = drop;

            // The same dumpster the game's other properties have, if we can find one to copy.
            if (GameProps.FindDumpster(null, out var model, out var standLocal, out var dropLocal))
            {
                try
                {
                    // The game's own skip, exactly as it is at the other properties: same model,
                    // same materials, same size (it's part of the static-batched level, so it's
                    // cut out of the combined mesh rather than copied - see CloneLevelPiece).
                    var copy = GameProps.CloneLevelPiece(model, "Dumpster", out int parts);
                    if (parts == 0) throw new System.Exception("no parts could be copied");
                    copy.transform.SetParent(d.transform, false);
                    copy.transform.localPosition = Vector3.zero;
                    // Turn it so the side cleaners stand at faces the middle of the platform.
                    var standSide = standLocal - dropLocal; standSide.y = 0f;
                    var inward = -pos; inward.y = 0f;
                    float yaw = Vector3.SignedAngle(Vector3.forward, inward, Vector3.up) - Vector3.SignedAngle(Vector3.forward, standSide, Vector3.up)
                              + DumpsterTurn;
                    copy.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                    // Sit it on the deck (lowest point of its meshes, worked out from the meshes -
                    // renderer bounds of objects made this frame aren't up to date yet).
                    float minY = float.MaxValue, len = 0f;
                    foreach (var mf in copy.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var mb = mf.sharedMesh.bounds;
                        for (int cx = 0; cx < 2; cx++) for (int cy = 0; cy < 2; cy++) for (int cz = 0; cz < 2; cz++)
                        {
                            var corner = mf.transform.TransformPoint(new Vector3(cx == 0 ? mb.min.x : mb.max.x, cy == 0 ? mb.min.y : mb.max.y, cz == 0 ? mb.min.z : mb.max.z));
                            minY = Mathf.Min(minY, corner.y);
                        }
                        len = Mathf.Max(len, Mathf.Max(mb.size.x, mb.size.z));
                    }
                    if (minY < float.MaxValue) copy.transform.position += Vector3.up * (d.transform.position.y - minY);
                    stand.position = copy.transform.TransformPoint(standLocal);
                    drop.position = copy.transform.TransformPoint(dropLocal);
                    Core.Log.Msg($"Dumpster: the game's '{model.name}' ({parts} part(s), {len:0.0} m long, game size), at {copy.transform.position}.");
                    return area;
                }
                catch (System.Exception e)
                {
                    Core.Log.Warning("Couldn't copy the game's dumpster: " + e.Message);
                }
            }

            // Fallback: a simple green dumpster.
            d.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            var green = Kit.Mat("dumpster", new Color(0.15f, 0.35f, 0.2f), 0.3f);
            var dark = Kit.Mat("dumpsterlid", new Color(0.1f, 0.1f, 0.1f), 0.3f);
            Kit.Box(d.transform, "Body", new Vector3(0f, 0.75f, 0f), new Vector3(2.4f, 1.3f, 1.4f), green);
            Kit.Box(d.transform, "Lid", new Vector3(0f, 1.45f, -0.35f), new Vector3(2.45f, 0.08f, 0.75f), dark, false, new Vector3(-20f, 0f, 0f));
            stand.localPosition = new Vector3(0f, 0.1f, 1.6f);
            drop.localPosition = new Vector3(0f, 1.6f, 0f);
            return area;
        }

        // Fixed IDs for the loading docks and their parking lots (one per bay): deliveries and
        // employee routes saved in a game point at a dock by its ID, so they must never change.
        private static string DockGuid(int i) => $"4a1c7e52-9b3d-4f60-8e21-6d5b0c9f7b{i + 1:00}";
        private static string LotGuid(int i) => $"4a1c7e52-9b3d-4f60-8e21-6d5b0c9f7c{i + 1:00}";

        // The parking bays (IslandBuilder.ParkingBays) as the game's own loading docks - what the
        // "cars" number on property listings counts, what the Deliveries app lets you deliver to,
        // and what employees can be told to fetch from. Each is a copy of a real dock (its yellow
        // bay markings, vehicle detector and parking lot included), lined up so its parking spot
        // is on the bay; the copy's plain meshes are switched off (they're the level's
        // static-batched ones - the markings are decals and stay). Bays are numbered 1 and 2 by
        // walkway 2, 3 and 4 by walkway 1, and each is copied from a dock with the same number
        // where the game has one, so its painted number matches.
        private static Il2CppReferenceArray<Il2CppScheduleOne.Delivery.LoadingDock> BuildLoadingDocks(Transform parent, Property prop)
        {
            var made = new System.Collections.Generic.List<Il2CppScheduleOne.Delivery.LoadingDock>();
            try
            {
                // Every usable dock in the game, with its number at its own property.
                var docks = new System.Collections.Generic.List<(Il2CppScheduleOne.Delivery.LoadingDock dock, int number)>();
                foreach (var p in Property.Properties)
                {
                    if (p == null || p == prop || p.LoadingDocks == null) continue;
                    for (int k = 0; k < p.LoadingDocks.Length; k++)
                    {
                        var d = p.LoadingDocks[k];
                        if (d != null && d.Parking != null && d.Parking.ParkingSpots != null && d.Parking.ParkingSpots.Count > 0) docks.Add((d, k + 1));
                    }
                }
                if (docks.Count == 0)
                {
                    Core.Log.Warning("No loading dock at another property to copy - the island has no parking.");
                    return new Il2CppReferenceArray<Il2CppScheduleOne.Delivery.LoadingDock>(0);
                }
                // Walkway 2's pair first (bays 1, 2), then walkway 1's (3, 4), each pair in order.
                var bays = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(IslandBuilder.ParkingBays, b => b.Walkway == "walkway 2" ? 0 : 1));

                for (int i = 0; i < bays.Count; i++)
                {
                    var bay = bays[i];
                    int number = i + 1;
                    var pick = docks.Find(x => x.number == number);
                    var template = pick.dock ?? docks[0].dock;
                    var spot = template.Parking.ParkingSpots[0].transform;
                    bool lotInside = template.Parking.transform.IsChildOf(template.transform);
                    // Where the template's pieces go: its parking spot onto the bay.
                    var move = Matrix4x4.TRS(bay.Position, bay.Rotation, Vector3.one) * Matrix4x4.TRS(spot.position, spot.rotation, Vector3.one).inverse;
                    var turn = bay.Rotation * Quaternion.Inverse(spot.rotation);

                    var holder = new GameObject("DockHolder");
                    holder.SetActive(false);
                    var dockGo = Object.Instantiate(template.gameObject, holder.transform);
                    dockGo.name = $"LoadingDock_{number}";
                    dockGo.transform.SetPositionAndRotation(move.MultiplyPoint3x4(template.transform.position), turn * template.transform.rotation);
                    var dock = dockGo.GetComponent<Il2CppScheduleOne.Delivery.LoadingDock>();

                    var lot = dock.Parking;
                    if (!lotInside)
                    {
                        var lotGo = Object.Instantiate(template.Parking.gameObject, holder.transform);
                        lotGo.name = $"ParkingLot_{number}";
                        lotGo.transform.SetPositionAndRotation(move.MultiplyPoint3x4(template.Parking.transform.position), turn * template.Parking.transform.rotation);
                        lot = lotGo.GetComponent<Il2CppScheduleOne.Map.ParkingLot>();
                        dock.Parking = lot;
                    }
                    // Same for its vehicle detector, if that isn't part of the dock itself.
                    GameObject detectorGo = null;
                    if (template.VehicleDetector != null && !template.VehicleDetector.transform.IsChildOf(template.transform))
                    {
                        detectorGo = Object.Instantiate(template.VehicleDetector.gameObject, holder.transform);
                        detectorGo.transform.SetPositionAndRotation(move.MultiplyPoint3x4(template.VehicleDetector.transform.position), turn * template.VehicleDetector.transform.rotation);
                        dock.VehicleDetector = detectorGo.GetComponent<Il2CppScheduleOne.DevUtilities.VehicleDetector>();
                    }

                    var roots = detectorGo != null ? new[] { dockGo, lot.gameObject, detectorGo } : new[] { dockGo, lot.gameObject };

                    // On the deck: slide the whole bay so its painted markings (not its parking
                    // spot, which sits off to one side of them) are centred where the bay goes.
                    if (bay.CentreMarkings)
                    {
                        var decal = dockGo.GetComponentInChildren<UnityEngine.Rendering.Universal.DecalProjector>(true)
                                    ?? lot.GetComponentInChildren<UnityEngine.Rendering.Universal.DecalProjector>(true);
                        if (decal != null)
                        {
                            var shift = bay.Position - decal.transform.TransformPoint(decal.pivot);
                            shift.y = 0f;
                            dockGo.transform.position += shift;
                            if (!lotInside) lot.transform.position += shift;
                            if (detectorGo != null) detectorGo.transform.position += shift;
                        }
                    }
                    foreach (var root in roots)
                    {
                        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                            if (r.GetComponent<Il2CppTMPro.TMP_Text>() == null) r.enabled = false;
                        foreach (var c in root.GetComponentsInChildren<Collider>(true)) if (!c.isTrigger) Object.DestroyImmediate(c);
                        foreach (var no in root.GetComponentsInChildren<NetworkObject>(true)) Object.DestroyImmediate(no);
                    }
                    if (pick.dock == null) Renumber(roots, number, docks);
                    // The bay on the island's deck is sprayed in black, not the road bays' yellow.
                    if (bay.CentreMarkings)
                        foreach (var root in roots)
                            foreach (var dp in root.GetComponentsInChildren<UnityEngine.Rendering.Universal.DecalProjector>(true))
                                if (BayDecals.Blacken(dp)) Core.Log.Msg($"Loading bay {number} is painted black.");
                    if (i == 0) DescribeDock(dockGo);

                    dock.BakedGUID = DockGuid(i);
                    lot.BakedGUID = LotGuid(i);
                    dock.ParentProperty = prop;

                    // Into the property (still switched off - they wake up with it).
                    dockGo.transform.SetParent(parent, true);
                    if (!lotInside) lot.transform.SetParent(parent, true);
                    if (detectorGo != null) detectorGo.transform.SetParent(parent, true);
                    Object.Destroy(holder);
                    made.Add(dock);
                }
                Core.Log.Msg($"Parking: {made.Count} loading bay(s) by the walkways.");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't set up the island's parking: " + e.Message);
            }
            var arr = new Il2CppReferenceArray<Il2CppScheduleOne.Delivery.LoadingDock>(made.Count);
            for (int i = 0; i < made.Count; i++) arr[i] = made[i];
            return arr;
        }

        // When no dock in the game already has this number painted on: number texts are set to
        // it, and number decals get the material another dock uses for that number.
        private static void Renumber(GameObject[] roots, int number, System.Collections.Generic.List<(Il2CppScheduleOne.Delivery.LoadingDock dock, int number)> docks)
        {
            string want = number.ToString();
            foreach (var root in roots)
            {
                foreach (var t in root.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true))
                    if (!string.IsNullOrEmpty(t.text) && int.TryParse(t.text.Trim(), out _)) t.text = want;
                foreach (var dp in root.GetComponentsInChildren<UnityEngine.Rendering.Universal.DecalProjector>(true))
                {
                    // The number is part of the decal's material ("Loading dock decal mat 1"):
                    // use the game's material for this number, wherever it's loaded.
                    var m = dp.material;
                    if (m == null || !System.Text.RegularExpressions.Regex.IsMatch(m.name, @"\d")) continue;
                    string wantName = System.Text.RegularExpressions.Regex.Replace(m.name, @"\d+(?!.*\d)", want);
                    Material found = null;
                    foreach (var other in Resources.FindObjectsOfTypeAll<Material>())
                        if (other != null && other.name == wantName) { found = other; break; }
                    if (found != null) dp.material = found;
                    else if (BayDecals.Number(dp, number)) Core.Log.Msg($"Loading bay {want}: the game has no '{wantName}', so its number is painted on.");
                    else Core.Log.Warning($"The game has no '{wantName}' - loading bay {want} shows the number of the bay it was copied from.");
                }
            }
        }

        // What a loading dock is made of (logged once), so its number painting can be traced.
        private static void DescribeDock(GameObject dockGo)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var t in dockGo.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true)) parts.Add($"text '{t.gameObject.name}'=\"{t.text}\"");
            foreach (var dp in dockGo.GetComponentsInChildren<UnityEngine.Rendering.Universal.DecalProjector>(true)) parts.Add($"decal '{dp.gameObject.name}' ({(dp.material != null ? dp.material.name : "no material")})");
            Core.Log.Msg("Loading dock parts: " + (parts.Count > 0 ? string.Join(", ", parts) : "no texts or decals"));
        }

        // No for-sale sign on the island. The game still needs the object: when a property starts
        // up it writes the name and price into ForSaleSign's "Name" and "Price" texts without
        // checking there's a sign - so this is an empty, switched-off stand-in with just those.
        private static GameObject BuildForSaleSign(Transform parent)
        {
            var sign = Child(parent, "ForSaleSign", Vector3.zero);
            foreach (var n in new[] { "Name", "Price" })
                Child(sign.transform, n, Vector3.zero).AddComponent<TextMeshPro>();
            sign.SetActive(false);
            return sign;
        }
    }

    // When the save's property data is read, make sure the island property exists first so the
    // game's own loader finds it (restoring ownership, then the items on its greenhouse grid).
    [HarmonyPatch(typeof(PropertyManager), nameof(PropertyManager.LoadProperty))]
    internal static class LoadPropertyPatch
    {
        // Returning false skips the game's load of this data.
        private static bool Prefix(PropertyData propertyData)
        {
            try
            {
                if (propertyData == null || propertyData.PropertyCode != JungleProperty.Code) return true;

                // Safeguard for the rename: the island's data must only ever load once, or its
                // placed items would be spawned twice.
                if (JungleProperty.LoadedFromSave)
                {
                    Core.Log.Warning("Skipped a second copy of the Greenhouse Island save data (left over from the old name).");
                    return false;
                }
                JungleProperty.LoadedFromSave = true;
                JungleProperty.RetireOldSaveFile();

                if (JungleProperty.Property == null && !JungleProperty.TryCreate())
                    Core.Log.Warning("Save has Greenhouse Island data but the property couldn't be created in time.");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("LoadProperty hook failed: " + e.Message);
            }
            return true;
        }
    }
}

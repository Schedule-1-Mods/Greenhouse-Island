using Il2CppScheduleOne.Building;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Tiles;
using Il2CppScheduleOne.Trash;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // A trash can on the outside deck, for the cleaners.
    //
    // How the game's cleaners work (Cleaner.TryStartNewTask): they only pick up trash lying within
    // the pickup square of a trash can (TrashContainerItem) that's assigned to them, bag it at the
    // can, and carry the bags to the property's disposal area (our dumpster). So the island needs
    // a real, placed trash can - the same one you buy at the hardware store - out on the deck.
    //
    // It's placed once per save (a marker file remembers that), on the deck's one small buildable
    // patch right of the entrance (the rest of the deck can't be built on), and given to every
    // cleaner working on the island. After that it's an ordinary placed item: saved by the game, and you
    // can pick it up and move it like any other.
    internal static class DeckBin
    {
        private static float _next;
        private static bool _done;
        private static float _ownedSince = -1f;

        public static void Clear() { _done = false; _ownedSince = -1f; _next = 0f; }

        public static void Tick()
        {
            if (_done || Time.time < _next) return;
            _next = Time.time + 5f;
            var prop = JungleProperty.Property;
            var grid = GreenhouseGrid.DeckGrid;
            var site = IslandBuilder.Current?.Site;
            if (prop == null || grid == null || site == null || !JungleProperty.IsOwned) return;
            if (_ownedSince < 0f) { _ownedSince = Time.time; return; }   // let the save's items load first

            try
            {
                var existing = FindDeckBin(prop, grid);
                if (existing != null)
                {
                    GiveToCleaners(prop, existing);
                    _done = true;
                    return;
                }
                string marker = MarkerPath();
                if (marker != null && System.IO.File.Exists(marker)) { _done = true; return; }   // placed before, since moved/removed by you

                // The deck's only buildable patch (see IslandBuilder.TrashCanSpot).
                var island = Quaternion.Inverse(Quaternion.LookRotation(site.Dir));
                var want = new Vector3(IslandBuilder.TrashCanSpot.x, 0f, IslandBuilder.TrashCanSpot.y);

                var def = FindTrashCan();
                if (def == null) { Core.Log.Warning("Couldn't find the game's trash can item to put on the deck."); _done = true; return; }

                // Deck tile nearest that spot with room round it for the can.
                Tile best = null; float bestD = float.MaxValue;
                var gridT = grid.transform;
                foreach (var tile in grid.Tiles)
                {
                    if (tile == null || !HasRoom(grid, tile.x, tile.y)) continue;
                    var local = island * (tile.transform.position - site.Center);
                    float d = new Vector2(local.x - want.x, local.z - want.z).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = tile; }
                }
                if (best == null) { Core.Log.Warning("No free deck tile for the trash can."); _done = true; return; }

                var bm = NetworkSingleton<BuildManager>.Instance;
                var item = bm.CreateGridItem(def.GetDefaultInstance(1), grid, new Vector2(best.x, best.y), 0, "", null);
                if (item == null) { Core.Log.Warning("The game didn't create the deck trash can."); _done = true; return; }

                if (marker != null)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(marker));
                    System.IO.File.WriteAllText(marker, System.DateTime.Now.ToString("u"));
                }
                var bin = item.TryCast<TrashContainerItem>();
                if (bin != null) GiveToCleaners(prop, bin);
                Core.Log.Msg($"Put a '{def.name}' on the deck at {best.transform.position}, right of the entrance, for the cleaners.");
                _done = true;
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't put the trash can on the deck: " + e.Message);
                _done = true;
            }
        }

        // Room for a small item: the tile and its neighbours all exist and are free.
        private static bool HasRoom(Grid grid, int x, int y)
        {
            for (int dx = -1; dx <= 2; dx++)
            for (int dy = -1; dy <= 2; dy++)
            {
                var t = grid.GetTile(new Coordinate(x + dx, y + dy));
                if (t == null || t.BuildableOccupants.Count > 0) return false;
            }
            return true;
        }

        private static TrashContainerItem FindDeckBin(Il2CppScheduleOne.Property.Property prop, Grid grid)
        {
            foreach (var b in prop.BuildableItems)
            {
                var bin = b == null ? null : b.TryCast<TrashContainerItem>();
                if (bin != null && bin.OwnerGrid == grid) return bin;
            }
            return null;
        }

        // The hardware store's trash can: the buildable whose built item is a trash container.
        private static BuildableItemDefinition FindTrashCan()
        {
            BuildableItemDefinition any = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<BuildableItemDefinition>())
            {
                if (d == null || d.BuiltItem == null || d.BuiltItem.GetComponent<TrashContainerItem>() == null) continue;
                // By the asset's Unity name, not the item ID: the ID lives on BaseItemDefinition,
                // which 0.4.7 moved into Il2CppScheduleOne.Core.dll - a file the main branch
                // (0.4.6) doesn't have, so touching it would break the mod there.
                if (d.name.ToLowerInvariant().Replace(" ", "").Replace("_", "") == "trashcan") return d;
                if (any == null) any = d;
            }
            return any;
        }

        private static void GiveToCleaners(Il2CppScheduleOne.Property.Property prop, TrashContainerItem bin)
        {
            int given = 0;
            foreach (var emp in prop.Employees)
            {
                var cleaner = emp == null ? null : emp.TryCast<Cleaner>();
                var cfg = cleaner?.Configuration?.TryCast<CleanerConfiguration>();
                if (cfg == null || cfg.Bins == null) continue;
                var list = cfg.Bins.SelectedObjects;
                bool has = false;
                foreach (var o in list) if (o != null && o.Pointer == bin.Pointer) { has = true; break; }
                if (has || list.Count >= cfg.Bins.MaxItems) continue;
                var next = new Il2CppSystem.Collections.Generic.List<BuildableItem>();
                foreach (var o in list) next.Add(o);
                next.Add(bin);
                cfg.Bins.SetList(next, true);
                given++;
            }
            if (given > 0) Core.Log.Msg($"Gave the deck trash can to {given} cleaner(s).");
        }

        private static string MarkerPath()
        {
            var lm = PersistentSingleton<Il2CppScheduleOne.Persistence.LoadManager>.Instance;
            string save = lm != null ? lm.LoadedGameFolderPath : null;
            if (string.IsNullOrEmpty(save)) return null;
            string name = System.IO.Path.GetFileName(save.TrimEnd('\\', '/'));
            return System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PelicanKey", "deck-bin-placed", name + ".txt");
        }
    }
}

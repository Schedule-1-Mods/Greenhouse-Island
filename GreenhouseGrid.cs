using Il2CppScheduleOne.Tiles;
using UnityEngine;
using Object = UnityEngine.Object;
using Property = Il2CppScheduleOne.Property.Property;

namespace PelicanKey
{
    // The greenhouse floor: a real build grid, the same kind of tile grid a property's floor has,
    // owned by the Greenhouse Island property. Placed items remember the grid's GUID, so it
    // is fixed and the grid is recreated before the property's items load.
    internal static class GreenhouseGrid
    {
        public static Grid Grid;        // ground floor
        public static Grid UpperGrid;   // glass second floor
        public static Grid DeckGrid;    // outside deck round the dome

        public enum Floor { Ground, Upper, Deck }

        // How far above a floor surface its tiles sit.
        public const float TileLift = 0.04f;

        public static void Clear() { Grid = null; UpperGrid = null; DeckGrid = null; }

        // floorY: height of the floor (island-local); include(x, z): whether a tile centre is buildable.
        public static bool Create(Property owner, Vector3 halfSize, string guid, float floorY, System.Func<float, float, bool> include, Floor floor)
        {
            if ((floor == Floor.Upper ? UpperGrid : floor == Floor.Deck ? DeckGrid : Grid) != null) return true;
            try
            {
                // Copy tiles from the biggest grid of any existing property.
                Grid template = null;
                foreach (var p in Property.Properties)
                {
                    if (p == null || p == owner || p.Grids == null) continue;
                    foreach (var g in p.Grids)
                        if (g != null && g.Tiles != null && g.Tiles.Count > 0 && (template == null || g.Tiles.Count > template.Tiles.Count))
                            template = g;
                }
                if (template == null)
                {
                    Core.Log.Warning("No existing build grid to copy tiles from.");
                    return false;
                }

                var tplTile = template.Tiles[0];
                float ts = Grid.TileSize;
                // Where a tile sits relative to its grid, beyond x/y * TileSize.
                var tplLocal = template.transform.InverseTransformPoint(tplTile.transform.position);
                var offset = tplLocal - new Vector3(tplTile.x * ts, 0f, tplTile.y * ts);
                Core.Log.Msg($"Build tiles ({floor}): the game's own tiles sit {offset.y:0.000} m off their grid; ours are lifted {TileLift:0.00} m above the floor so the placement squares show.");
                var tileRot = Quaternion.Inverse(template.transform.rotation) * tplTile.transform.rotation;

                int w = Mathf.FloorToInt(halfSize.x * 2f / ts);
                int h = Mathf.FloorToInt(halfSize.z * 2f / ts);

                // Build inactive so Awake runs only once everything is filled in.
                var holder = new GameObject($"JungleGreenhouse{floor}GridHolder");
                holder.SetActive(false);
                // Must sit inside the property's hierarchy: placed items find their property by
                // walking up from the grid (and are parented to the property's contents container).
                holder.transform.SetParent(owner.transform, false);
                // Tiles (and the placement squares the game draws on them, and where items stand) sit
                // a few cm above the floor surface: right on it, the planks (and the glass floor
                // upstairs) hide the squares.
                holder.transform.localPosition = new Vector3(-w * ts * 0.5f, floorY + TileLift, -h * ts * 0.5f);

                var go = new GameObject($"JungleGreenhouse{floor}Grid");
                go.transform.SetParent(holder.transform, false);
                go.layer = template.gameObject.layer;

                var grid = go.AddComponent<Grid>();
                grid._guid = guid;
                grid._parentProperty = owner;

                int made = 0;
                for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    // Only the tiles the floor allows (see include).
                    float cx = (x + 0.5f) * ts - w * ts * 0.5f, cz = (y + 0.5f) * ts - h * ts * 0.5f;
                    if (!include(cx, cz)) continue;
                    made++;

                    var tgo = Object.Instantiate(tplTile.gameObject, go.transform);
                    tgo.name = $"Tile ({x}, {y})";
                    tgo.transform.localPosition = new Vector3(x * ts, 0f, y * ts) + offset;
                    tgo.transform.localRotation = tileRot;
                    var tile = tgo.GetComponent<Tile>();
                    tile.InitializePropertyTile(x, y, tplTile.AvailableOffset, grid);
                    grid.CoordinateTilePairs.Add(new CoordinateTilePair { coord = new Coordinate(x, y), tile = tile });
                }

                holder.SetActive(true);

                // Make sure the lookups are filled even if Awake handled things differently.
                if (grid.Tiles.Count == 0)
                    foreach (var t in go.GetComponentsInChildren<Tile>(true)) grid.RegisterTile(t);
                if (grid._coordinateToTile == null || grid._coordinateToTile.Count == 0)
                    grid.ProcessCoordinateDataPairs();

                if (!owner.Grids.Contains(grid)) owner.Grids.Add(grid);
                if (floor == Floor.Upper) UpperGrid = grid; else if (floor == Floor.Deck) DeckGrid = grid; else Grid = grid;

                Core.Log.Msg($"Greenhouse {(floor == Floor.Upper ? "upstairs" : floor == Floor.Deck ? "outside deck" : "ground floor")} build grid ready: {made} tiles ({ts} m each), {grid.Tiles.Count} registered.");
                return true;
            }
            catch (System.Exception e)
            {
                Core.Log.Error("Couldn't create the greenhouse build grid: " + e);
                return false;
            }
        }
    }
}

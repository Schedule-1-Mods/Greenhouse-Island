using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

[assembly: MelonInfo(typeof(PelicanKey.Core), "Greenhouse Island", "1.0.0", "Cassy")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace PelicanKey
{
    // Adds Greenhouse Island: a floating platform off the Docks, reached by walkways from
    // the shore, with a glass greenhouse dome round a big weed plant. Buy it from Ray for $0:
    // the door unlocks, the save point by the door works, and the dome floors become real build
    // grids, with room for employees on the deck.
    //
    // The scenery is built locally at runtime; the property and what you build on it are saved
    // by the game itself. The island is always in the same place for everyone (see FixedCenter),
    // and there are no hotkeys - you walk there like anywhere else.
    public class Core : MelonMod
    {
        // Where the island is, for everyone: the platform's centre, and where each walkway starts
        // on the shore (x, y, z). Fixed, so every player's island - and everything they've built
        // on it - is in the same place.
        private static readonly Vector3 FixedCenter = new Vector3(-98.19962f, 0f, 0.58373f);
        private static readonly List<Vector3> FixedWalkways = new List<Vector3>
        {
            new Vector3(-129.15f, -10f, 15.85f),
            new Vector3(-82.6f, -2.5f, -24.94f),
        };

        internal static MelonLogger.Instance Log;

        internal static MelonPreferences_Category Prefs;
        internal static MelonPreferences_Entry<bool> Enabled;
        internal static MelonPreferences_Entry<int> EmployeeCapacity;

        private bool _inMain;
        private float _spikesFrom, _nextSpikeLog;
        private int _spikes;
        private float _nextLightCheck;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;

            Prefs = MelonPreferences.CreateCategory("PelicanKey", "Greenhouse Island");
            Enabled = Prefs.CreateEntry("Enabled", true, "Build Greenhouse Island");
            EmployeeCapacity = Prefs.CreateEntry("EmployeeCapacity", 30, "How many employees the island can hold (0-30)");
            TreeColours.Init(Prefs);
            WindmillLights.Init(Prefs);

            MelonEvents.OnGUI.Subscribe(Hud.Draw);
            MelonEvents.OnGUI.Subscribe(TreeColours.Draw);
            MelonEvents.OnGUI.Subscribe(WindmillLights.Draw);
            Log.Msg("Greenhouse Island loaded. Walk down the walkway from the Docks to get there.");
        }

        // Every mod is loaded by now, so other mods we add Greenhouse Island to can be found.
        public override void OnLateInitializeMelon()
        {
            try { WagesManagerPatch.TryPatch(HarmonyInstance); }
            catch (System.Exception e) { Log.Warning("Couldn't hook into the Wages Manager app: " + e.Message); }
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            try { WagesManagerPatch.TryPatch(HarmonyInstance); } catch { }
            _inMain = sceneName == "Main";
            _spikesFrom = Time.time + 60f; _spikes = 0;
            DeckBin.Clear();
            IslandBuilder.Clear(true);
            if (!_inMain || !Enabled.Value) return;

            // Build the scenery straight away, before the save loads: if this save owns the island,
            // its property is created the moment the game loads property data (LoadPropertyPatch)
            // and needs the island - and its greenhouse floor - to already be there.
            Rebuild();
            JungleProperty.RetireOldSaveFile(); // before the game reads the save's property files
            JungleProperty.TryCreate();

            MelonCoroutines.Start(AfterLoad());
        }

        private static IEnumerator AfterLoad()
        {
            float waited = 0f;
            while (Player.Local == null && waited < 180f)
            {
                waited += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }
            yield return new WaitForSeconds(5f);

            // If the coast wasn't ready to scan during scene init, try again now.
            if (IslandBuilder.Current == null) Rebuild();

            // Not bought on this save yet (so nothing loaded it): put it on the market now.
            for (int i = 0; i < 12 && !JungleProperty.TryCreate(); i++)
                yield return new WaitForSeconds(5f);
            if (JungleProperty.Property == null)
                Log.Warning("Couldn't list Greenhouse Island for sale (the island property needs you to be the host or playing single player).");
            else
            {
                JungleProperty.RescueStrandedEmployees();
                JungleProperty.MakeListingPhoto();
                JungleProperty.LogEmployeeStatus();
                EmployeeCheck.Start();
            }
        }

        internal static void Rebuild()
        {
            IslandBuilder.Clear(false);
            try
            {
                var walkways = new List<Vector3>(FixedWalkways);
                var toWarehouse = WarehouseWalkwayStart();
                if (toWarehouse.HasValue) walkways.Add(toWarehouse.Value);
                var site = Site.Create(FixedCenter, walkways);
                // The island always faces walkway 2, as it always has - not whichever walkway is
                // nearest - so the dome, its doors and build grids (and what's built on them) stay put.
                var face = site.Center - FixedWalkways[1]; face.y = 0f;
                site.Dir = face.normalized;
                site.Right = Vector3.Cross(Vector3.up, site.Dir);
                IslandBuilder.Build(site);
                JungleProperty.Reposition();
            }
            catch (System.Exception e)
            {
                Log.Error("Failed to build Greenhouse Island: " + e);
            }
        }

        // Walkway 3: a bridge from the platform to the Docks Warehouse. It starts where a straight
        // line from the island towards the warehouse first reaches the quay - the same spot in
        // every game.
        private static Vector3? WarehouseWalkwayStart()
        {
            try
            {
                // The warehouse where you hire employees (Manny's) - in the game's code that's the
                // "dark market" building; failing that, wherever Manny (the Fixer) is standing.
                Vector3? warehouse = null;
                string aimed = null;
                var market = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.Map.DarkMarket>();
                if (market != null)
                {
                    warehouse = market.MainDoor != null ? market.MainDoor.transform.position : market.transform.position;
                    aimed = "the warehouse (Manny's)";
                }
                else
                {
                    var fixer = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.NPCs.CharacterClasses.Fixer>();
                    if (fixer != null) { warehouse = fixer.transform.position; aimed = "Manny"; }
                }
                if (!warehouse.HasValue) { Log.Warning("Couldn't find the warehouse where you hire employees - no walkway to it."); return null; }
                Log.Msg($"Walkway 3 heads for {aimed} at ({warehouse.Value.x:0}, {warehouse.Value.z:0}).");

                float sea = Siting.SeaLevel();
                var dir = warehouse.Value - FixedCenter; dir.y = 0f;
                dir.Normalize();
                for (float t = IslandBuilder.PlatformRadius + 2f; t < 250f; t += 0.5f)
                {
                    var p = FixedCenter + dir * t;
                    var h = Siting.GroundHeight(p.x, p.z, null);
                    if (h.HasValue && h.Value > sea + 0.3f)
                    {
                        var start = p + dir * 1.5f;
                        start.y = Siting.GroundHeight(start.x, start.z, null) ?? h.Value;
                        Log.Msg($"Walkway 3 starts on the quay at ({start.x:0.0}, {start.y:0.0}, {start.z:0.0}).");
                        return start;
                    }
                }
                Log.Warning("No quay between the island and the Docks Warehouse - no walkway to it.");
            }
            catch (System.Exception e)
            {
                Log.Warning("Couldn't work out the walkway to the Docks Warehouse: " + e.Message);
            }
            return null;
        }

        public override void OnUpdate()
        {
            if (!_inMain) return;

            // Hitches: any frame over 100 ms (after the first minute of loading in) goes in the
            // log with its time, to match against what else was logged then. At most one a second.
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.1f && Time.time > _spikesFrom && Time.unscaledTime >= _nextSpikeLog && _spikes < 300)
            {
                _nextSpikeLog = Time.unscaledTime + 1f;
                _spikes++;
                Log.Msg($"[FrameSpike] {dt * 1000f:0} ms frame.");
            }

            IslandBuilder.Tick(Time.deltaTime);
            IslandNavMesh.Tick();
            EmployeeCheck.Tick();
            DeckBin.Tick();
            TreeColours.Tick();
            WindmillLights.Tick();
            RainShelter.Tick();
            try { RainShelter.Frame(); } catch { }

            if (Time.time >= _nextLightCheck)
            {
                _nextLightCheck = Time.time + 2f;
                bool night = true;
                try
                {
                    var tm = NetworkSingleton<TimeManager>.Instance;
                    if (tm != null) night = tm.IsNight;
                }
                catch { }
                IslandBuilder.SetNight(night);
            }
        }
    }
}

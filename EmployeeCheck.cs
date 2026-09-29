using System.Collections.Generic;
using Il2CppScheduleOne.Employees;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Why aren't the island's employees working? For a few minutes after a save loads, every
    // 20 seconds, this logs each of them: what they're doing, the reasons the game gives for not
    // working, whether they stand on a walkable area, and whether they can walk to their work
    // (a botanist's supplies and pots). Only logs when something changed.
    internal static class EmployeeCheck
    {
        private static float _next, _until;
        private static readonly Dictionary<string, string> _last = new Dictionary<string, string>();

        public static void Start()
        {
            _next = Time.time + 10f;
            _until = Time.time + 300f;
            _last.Clear();
        }

        public static void Tick()
        {
            if (Time.time < _next || Time.time > _until) return;
            _next = Time.time + 20f;
            var prop = JungleProperty.Property;
            if (prop == null) return;
            try
            {
                foreach (var emp in prop.Employees)
                {
                    if (emp == null) continue;
                    string name = ((Object)emp).name;
                    string line = Describe(emp);
                    if (_last.TryGetValue(name, out var was) && was == line) continue;
                    _last[name] = line;
                    Core.Log.Msg($"[EmployeeCheck] {name}: {line}");
                }
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("[EmployeeCheck] " + e.Message);
            }
        }

        private static string Describe(Employee emp)
        {
            var parts = new List<string>();
            try
            {
                var active = emp.Behaviour != null ? emp.Behaviour.activeBehaviour : null;
                parts.Add("doing=" + (active != null ? ((Object)active).name : "nothing"));
            }
            catch { parts.Add("doing=?"); }
            parts.Add($"locker={(emp.GetHome() != null ? "yes" : "NONE")}, paid={emp.PaidForToday}");
            try { parts.Add("pathFails=" + emp.consecutivePathingFailures); } catch { }

            NavMeshAgent agent = null;
            try { agent = emp.Movement != null ? emp.Movement.Agent : null; } catch { }
            if (agent != null)
            {
                var at = agent.transform.position;
                bool onIsland = NavMesh.SamplePosition(at, out var hit, 1.5f, 1 << IslandNavMesh.IslandArea);
                parts.Add($"onNavMesh={agent.isOnNavMesh}, mask=0x{agent.areaMask:X}, onIslandArea={onIsland}");
                // Can they walk into the dome at all?
                var centre = JungleProperty.Property.transform.position;
                if (IslandBuilder.Current != null) centre = IslandBuilder.Current.Site.Center;
                parts.Add("pathToDome=" + PathTo(agent, centre));
            }
            else parts.Add("no NavMeshAgent");

            try
            {
                var botanist = emp.TryCast<Botanist>();
                if (botanist != null && botanist.configuration != null)
                {
                    var cfg = botanist.configuration;
                    var supplies = cfg.Supplies != null ? cfg.Supplies.SelectedObject : null;
                    // The game's own test before a job: within 1 m of one of the stash's access points.
                    parts.Add("supplies=" + (supplies == null ? "NONE" : "reach " + emp.Movement.CanGetTo(botanist.GetSuppliesAsTransitEntity(), 1f)));
                    int pots = cfg.Assigns != null ? cfg.Assigns.SelectedObjects.Count : 0;
                    parts.Add($"assigned={pots}");
                    if (pots > 0)
                    {
                        var first = cfg.Assigns.SelectedObjects[0];
                        if (first != null) parts.Add("firstPot reach " + emp.Movement.CanGetTo(first.transform.position, 2f));
                    }
                }
            }
            catch (System.Exception e) { parts.Add("botanist? " + e.Message); }

            try
            {
                var reasons = new List<string>();
                foreach (var w in emp.WorkIssues) if (w != null) reasons.Add(w.Reason);
                if (reasons.Count > 0) parts.Add("says: " + string.Join(" | ", reasons));
            }
            catch { }
            return string.Join(", ", parts);
        }

        private static string PathTo(NavMeshAgent agent, Vector3 target)
        {
            if (!NavMesh.SamplePosition(target, out var hit, 3f, NavMesh.AllAreas)) return "no walkable ground there";
            var path = new NavMeshPath();
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            NavMesh.CalculatePath(agent.transform.position, hit.position, filter, path);
            return path.status.ToString();
        }
    }
}

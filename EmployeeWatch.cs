using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Keeps the island's employees from getting stuck. Every couple of seconds (cheap: no path
    // searches) it looks at each employee standing on the platform:
    //
    //  - not on the walkable map at all (the game can't move them, and keeps failing to "create
    //    their agent", which floods the log and costs frame rate): set back down on the nearest
    //    walkable spot;
    //  - standing in the plant's planter at the middle of the dome (they stay wedged there,
    //    until the next sleep or a restart): lifted out onto the floor beside it.
    internal static class EmployeeWatch
    {
        private static float _next;
        private static readonly Dictionary<string, float> _logged = new Dictionary<string, float>();

        public static void Tick()
        {
            if (Time.time < _next) return;
            _next = Time.time + 2f;
            var prop = JungleProperty.Property;
            var site = IslandBuilder.Current?.Site;
            if (prop == null || site == null) return;

            try
            {
                foreach (var emp in prop.Employees)
                {
                    var agent = emp != null && emp.Movement != null ? emp.Movement.Agent : null;
                    if (agent == null) continue;

                    var p = emp.transform.position;
                    var flat = new Vector2(p.x - site.Center.x, p.z - site.Center.z);
                    // Only the platform itself (walkways and land are the game's to look after).
                    if (flat.magnitude > site.Radius || Mathf.Abs(p.y - site.Center.y) > 3f) continue;

                    bool inPlanter = flat.magnitude < IslandBuilder.TreePlanterRadius + 0.3f;
                    bool offMap = !agent.isOnNavMesh;
                    if (!inPlanter && !offMap) continue;

                    // Where to: straight out from the middle (keeping the side they're on) if in
                    // the planter, else the closest walkable spot to where they stand.
                    Vector3 from = p;
                    if (inPlanter)
                    {
                        var dir = flat.sqrMagnitude > 0.01f ? flat.normalized : new Vector2(0f, -1f);
                        float r = IslandBuilder.TreePlanterRadius + 1.4f;
                        from = new Vector3(site.Center.x + dir.x * r, p.y, site.Center.z + dir.y * r);
                    }
                    if (!NavMesh.SamplePosition(from, out var hit, 4f, agent.areaMask) && !NavMesh.SamplePosition(from, out hit, 8f, NavMesh.AllAreas))
                        continue;

                    Compat.Warp(emp.Movement, hit.position);

                    string name = ((Object)emp).name;
                    if (!_logged.TryGetValue(name, out float last) || Time.time - last > 30f)
                    {
                        _logged[name] = Time.time;
                        Core.Log.Msg($"[EmployeeWatch] {name} was {(inPlanter ? "stuck in the plant's planter" : "off the walkable map")} at {p}; set back on the floor at {hit.position}.");
                    }
                }
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("[EmployeeWatch] " + e.Message);
            }
        }
    }
}

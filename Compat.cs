using System.Linq;
using System.Reflection;
using Il2CppScheduleOne.NPCs;
using UnityEngine;

namespace PelicanKey
{
    // Game calls whose exact form differs between Steam branches. The mod is built once and has to
    // run on all of them (Default Public 0.4.6, beta 0.4.7, ...), so these are looked up by name
    // at runtime and called with whatever parameters the running game's version takes.
    internal static class Compat
    {
        private static MethodInfo _warp;

        // An NPC's walking agent. 0.4.6 has it as NPCMovement.Agent; 0.4.7 took that member away
        // (it's in a private field now), so ask for the NavMeshAgent component itself, which is
        // the same object on both.
        public static UnityEngine.AI.NavMeshAgent AgentOf(NPCMovement movement)
        {
            if (movement == null) return null;
            try { return movement.GetComponent<UnityEngine.AI.NavMeshAgent>() ?? movement.GetComponentInParent<UnityEngine.AI.NavMeshAgent>(); }
            catch { return null; }
        }

        // NPCMovement.Warp: 0.4.6 has Warp(Vector3), 0.4.7 has Warp(Vector3, Quaternion).
        public static void Warp(NPCMovement movement, Vector3 position)
        {
            if (movement == null) return;
            if (_warp == null)
                _warp = typeof(NPCMovement).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Warp")
                    .Select(m => (m, p: m.GetParameters()))
                    .Where(x => x.p.Length >= 1 && x.p[0].ParameterType == typeof(Vector3))
                    .OrderBy(x => x.p.Length)
                    .Select(x => x.m)
                    .FirstOrDefault();
            if (_warp == null)
            {
                // Last resort: put them there directly.
                movement.transform.position = position;
                return;
            }
            var ps = _warp.GetParameters();
            var args = new object[ps.Length];
            args[0] = position;
            for (int i = 1; i < ps.Length; i++)
                args[i] = ps[i].ParameterType == typeof(Quaternion) ? movement.transform.rotation
                        : ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : ps[i].ParameterType.IsValueType ? System.Activator.CreateInstance(ps[i].ParameterType) : null;
            _warp.Invoke(movement, args);
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // Writes to the log each time the game warps (teleports) one of the island's employees, with
    // how far. The game does it when an employee has failed to walk to a destination five times in
    // a row, and puts them right at it - which is what "teleporting to the work stations" is. Knowing
    // how often and from where says whether the walking map is at fault.
    internal static class WarpWatch
    {
        private static bool _patched;
        private static readonly Dictionary<System.IntPtr, float> _last = new Dictionary<System.IntPtr, float>();

        public static void TryPatch(HarmonyLib.Harmony harmony)
        {
            if (_patched) return;
            try
            {
                var warp = typeof(NPCMovement).GetMethod("Warp", new[] { typeof(Vector3) });
                if (warp == null) return;   // (a game version with a different Warp: nothing to watch)
                harmony.Patch(warp, prefix: new HarmonyMethod(typeof(WarpWatch).GetMethod(nameof(Before), BindingFlags.NonPublic | BindingFlags.Static)));
                _patched = true;
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't watch employee warps: " + e.Message);
            }
        }

        private static void Before(NPCMovement __instance, Vector3 position)
        {
            try
            {
                var prop = JungleProperty.Property;
                if (prop == null || __instance == null) return;
                var emp = __instance.GetComponentInParent<Employee>();
                if (emp == null || emp.AssignedProperty == null || emp.AssignedProperty.Pointer != prop.Pointer) return;
                if (_last.TryGetValue(emp.Pointer, out float t) && Time.time - t < 1f) return;
                _last[emp.Pointer] = Time.time;
                var from = emp.transform.position;
                Core.Log.Msg($"[Warp] {((Object)emp).name} warped {Vector3.Distance(from, position):0.0} m, from {from} to {position}.");
            }
            catch { }
        }
    }
}

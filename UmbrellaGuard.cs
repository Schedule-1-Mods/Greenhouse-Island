using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using UnityEngine;

namespace PelicanKey
{
    // Nobody under the dome needs an umbrella, but the game works out umbrellas from the weather
    // at a person's position and doesn't know the dome is a roof: employees (and dealers, who
    // walk through) put one up inside whenever it rains. This hooks the game's own switch for
    // it (NPC.HasUmbrella) so that, for anyone standing under the dome, "on" is turned into
    // "off" - whoever asks for it, the game itself or another mod. Employees who are already
    // holding one when they step in have it taken away by EmployeeWatch.
    internal static class UmbrellaGuard
    {
        private static bool _patched;
        private static MethodInfo _setter;

        public static void TryPatch(HarmonyLib.Harmony harmony)
        {
            if (_patched) return;
            try
            {
                _setter = typeof(NPC).GetProperty("HasUmbrella", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);
                if (_setter == null) return;   // (a game version without it: nothing to guard)
                harmony.Patch(_setter, prefix: new HarmonyMethod(typeof(UmbrellaGuard).GetMethod(nameof(Before), BindingFlags.NonPublic | BindingFlags.Static)));
                _patched = true;
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't hook the umbrella switch: " + e.Message);
            }
        }

        private static void Before(NPC __instance, ref bool value)
        {
            try
            {
                if (value && __instance != null && RainShelter.UnderDome(__instance.transform.position)) value = false;
            }
            catch { }
        }

        // Put down an umbrella someone is holding under the dome.
        public static void Lower(NPC npc)
        {
            try
            {
                if (_setter == null || npc == null || !npc.HasUmbrella) return;
                if (RainShelter.UnderDome(npc.transform.position)) _setter.Invoke(npc, new object[] { false });
            }
            catch { }
        }
    }
}

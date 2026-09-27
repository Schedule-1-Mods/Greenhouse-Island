using HarmonyLib;
using Il2CppScheduleOne.Map;
using UnityEngine;

namespace PelicanKey
{
    // The game's sea is ringed with WaterCollider triggers that warp anyone who touches them back
    // to shore. Greenhouse Island stands in the sea, so skip the warp for anyone who is above the
    // waterline on the island, bridge or pier. Actually falling in still warps as normal.
    [HarmonyPatch(typeof(WaterCollider), nameof(WaterCollider.OnTriggerEnter))]
    internal static class WaterPatch
    {
        private static bool Prefix(Collider other)
        {
            try
            {
                if (other == null) return true;
                var site = IslandBuilder.Current;
                if (site == null) return true;
                return !site.InZone(other.transform.position);
            }
            catch
            {
                return true;
            }
        }
    }
}

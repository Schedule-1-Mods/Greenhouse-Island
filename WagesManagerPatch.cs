using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PelicanKey
{
    // Adds Greenhouse Island to the Wages Manager phone app (the separate "WagesManager" mod), if
    // it's installed.
    //
    // That app has a fixed list of the game's properties (WagesManagerApp.LOCATION_NAMES, matched
    // against each owned property's object name, plus LOCATION_DISPLAY_NAMES for the labels) and
    // builds one row and one page per entry in WagesManagerApp.Create(). So just before Create()
    // runs, the island is added to both lists - it then gets its own row with today's wages, its
    // own page of employees, "pay all" and auto-pay, exactly like the game's properties.
    // Nothing here depends on that mod: if it isn't installed, this does nothing.
    internal static class WagesManagerPatch
    {
        private static bool _patched;

        public static void TryPatch(HarmonyLib.Harmony harmony)
        {
            if (_patched) return;
            var app = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType("WagesManager.WagesManagerApp", false); } catch { return null; } })
                .FirstOrDefault(t => t != null);
            if (app == null) return;
            var create = app.GetMethod("Create", BindingFlags.Public | BindingFlags.Instance);
            if (create == null) return;
            harmony.Patch(create, prefix: new HarmonyMethod(typeof(WagesManagerPatch).GetMethod(nameof(BeforeCreate), BindingFlags.NonPublic | BindingFlags.Static)));
            _patched = true;
            Core.Log.Msg("Wages Manager app found - Greenhouse Island will be listed in it.");
        }

        private static void BeforeCreate(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                var names = t.GetField("LOCATION_NAMES")?.GetValue(__instance) as List<string>;
                var labels = t.GetField("LOCATION_DISPLAY_NAMES")?.GetValue(__instance) as Dictionary<string, string>;
                if (names == null || labels == null)
                {
                    Core.Log.Warning("The Wages Manager app has changed - couldn't add Greenhouse Island to it.");
                    return;
                }
                if (!names.Contains(JungleProperty.ObjectName)) names.Add(JungleProperty.ObjectName);
                labels[JungleProperty.ObjectName] = "Greenhouse";   // short, like the app's other labels
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't add Greenhouse Island to the Wages Manager app: " + e.Message);
            }
        }
    }
}

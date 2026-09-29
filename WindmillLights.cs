using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.PlayerScripts;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;

namespace PelicanKey
{
    // The wind turbine's lights - the lamp in the middle of the blades and the lights along them.
    // Press E on the turbine's mast for a menu to change their colour (same panel as the plant's).
    // The colour is kept in the mod's settings.
    internal static class WindmillLights
    {
        public static readonly Color Default = new Color(1f, 0f, 161f / 255f);   // pink, #FF00A1

        private static MelonPreferences_Entry<string> _hex;
        private static Color _colour;
        private static readonly List<(Material mat, float glow)> _mats = new List<(Material, float)>();
        private static readonly List<Light> _lights = new List<Light>();

        public static bool Visible { get; private set; }

        public static void Init(MelonPreferences_Category prefs)
        {
            _hex = prefs.CreateEntry("WindmillLightColour", "#" + ColorUtility.ToHtmlStringRGB(Default), "Colour of the wind turbine's lights");
            _colour = Parse(_hex.Value);
        }

        private static Color Parse(string hex) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Default;

        public static void Clear() { _mats.Clear(); _lights.Clear(); if (Visible) Close(); }

        // A glowing material in the lights' colour (glow: how bright).
        public static Material Bulb(string name, float glow)
        {
            var m = Kit.Glow("windmill_" + name, _colour, glow);
            _mats.Add((m, glow));
            Paint();
            return m;
        }

        public static Light Lamp(Transform parent, Vector3 at, float range, float intensity)
        {
            var l = Kit.AddLight(parent, at, _colour, range, intensity);
            _lights.Add(l);
            Paint();
            return l;
        }

        // The lights stay on around the clock, day and night. (Kept as a call from the island's
        // day/night switch, which no longer matters here.)
        public static void SetNight(bool night) { }

        private static void Paint()
        {
            const bool on = true;
            // Off: the bulbs are just unlit glass, a dull tint of their colour.
            var body = on ? _colour : Color.Lerp(_colour, new Color(0.35f, 0.35f, 0.38f), 0.7f) * 0.6f;
            body.a = 1f;
            foreach (var (m, glow) in _mats)
            {
                if (m == null) continue;
                m.color = body;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", body);
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", on ? _colour * glow : Color.black);
            }
            foreach (var l in _lights)
                if (l != null) { l.color = _colour; l.enabled = on; }
        }

        // "E  Change turbine lights" on the mast.
        public static void AddInteraction(Transform turbine)
        {
            var go = new GameObject("LightColourMenu");
            go.transform.SetParent(turbine, false);
            _menuAt = go.transform;
            go.layer = TreeColours.InteractionLayer();
            var col = go.AddComponent<CapsuleCollider>();
            col.isTrigger = true;                       // found by the E raycast, but doesn't block you
            col.radius = 0.7f;
            col.height = 3f;
            col.center = Vector3.up * 1.5f;
            var io = go.AddComponent<InteractableObject>();
            io.SetMessage("Change turbine lights");
            io.MaxInteractionRange = 5f;
            io.onInteractStart.AddListener((UnityAction)(System.Action)Open);
        }

        private static void Open()
        {
            if (Visible || TreeColours.Visible) return;
            Visible = true;
            Paint();   // lit while you pick, even by day
            _picker.Set(_colour);
            ColourWidgets.FocusedField = null;
            SetPlayerFree(true);
        }

        private static void SaveAndClose()
        {
            _hex.Value = "#" + ColorUtility.ToHtmlStringRGB(_colour);
            Core.Prefs.SaveToFile(false);
            Hud.Toast("Turbine light colour saved.");
            Close();
        }

        // Closing without saving (Esc) puts the saved colour back.
        public static void Close()
        {
            if (!Visible) return;
            Visible = false;
            ColourWidgets.FocusedField = null;
            SetPlayerFree(false);
            _colour = Parse(_hex.Value);
            Paint();
        }

        private static void SetPlayerFree(bool menuOpen)
        {
            try
            {
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null)
                {
                    if (menuOpen) { cam.AddActiveUIElement("WindmillLights"); cam.FreeMouse(); cam.SetCanLook(false); }
                    else { cam.RemoveActiveUIElement("WindmillLights"); cam.LockMouse(); cam.SetCanLook(true); }
                }
                var move = PlayerSingleton<PlayerMovement>.Instance;
                if (move != null) move.CanMove = !menuOpen;
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv != null) inv.SetInventoryEnabled(!menuOpen);
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't switch the mouse for the windmill light menu: " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!Visible) return;
            if (Input.GetKeyDown(KeyCode.Escape) && ColourWidgets.FocusedField == null) { Close(); return; }
            // Walked (or got pushed) away from the turbine: the menu goes, like closing it.
            if (ColourWidgets.TooFarFrom(_menuAt, 6f)) Close();
        }

        private static Transform _menuAt;

        private static readonly Rect WindowRect = new Rect(20f, 20f, 340f, 330f);
        private static readonly ColourWidgets.Picker _picker = new ColourWidgets.Picker("windmillHex", "PelicanKey.light_icon.png");

        public static void Draw()
        {
            if (!Visible) return;
            GUI.Window(848312, WindowRect, (GUI.WindowFunction)DrawWindow, "Turbine Lights");
        }

        private static void DrawWindow(int id)
        {
            if (ColourWidgets.DrawPicker("Light Colour", _picker))
            {
                _colour = _picker.Colour;
                Paint();
            }
            GUILayout.Space(14f);
            if (GUILayout.Button("Restore to Default", GUILayout.Width(ColourWidgets.ContentWidth), GUILayout.Height(24f)))
            {
                _colour = Default;
                _picker.Set(_colour);
                Paint();
            }
            GUILayout.Space(6f);
            if (GUILayout.Button("Save", GUILayout.Width(ColourWidgets.ContentWidth), GUILayout.Height(28f))) SaveAndClose();
            GUILayout.Label("Esc: close without saving");
        }
    }
}

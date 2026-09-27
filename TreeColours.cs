using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.PlayerScripts;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;

namespace PelicanKey
{
    // The big weed plant's colours: press E on its trunk for a menu that recolours the plant
    // (fan leaves and stalks) and its buds separately. Colours are kept in the mod's settings.
    //
    // The E prompt is the game's own InteractableObject (the same thing the save intercom and every
    // other "press E" object uses): the game's InteractionManager raycasts for colliders on its
    // interaction layers, finds the InteractableObject above them and calls onInteractStart.
    internal static class TreeColours
    {
        // Pale pink plant, hot pink buds.
        public static readonly Color DefaultTree = new Color(1.00f, 0.72f, 0.82f);
        public static readonly Color DefaultBud = new Color(0.93f, 0.38f, 0.64f);

        private static MelonPreferences_Entry<string> _treeHex, _budHex;
        private static MelonPreferences_Entry<bool> _swapped;
        private static Color _tree, _bud;

        // Every recoloured material, with which part it's on and how much lighter/darker than the
        // chosen colour it's drawn (so leaves, stalks and the layers of a bud still read apart).
        private static readonly List<(Material mat, bool bud, float shade)> _mats = new List<(Material, bool, float)>();

        public static bool Visible { get; private set; }

        public static void Init(MelonPreferences_Category prefs)
        {
            _treeHex = prefs.CreateEntry("TreeColour", "#" + ColorUtility.ToHtmlStringRGB(DefaultTree), "Colour of the big plant's leaves and stalks");
            _budHex = prefs.CreateEntry("BudColour", "#" + ColorUtility.ToHtmlStringRGB(DefaultBud), "Colour of the big plant's buds");
            _swapped = prefs.CreateEntry("PlantBudColoursSwapped", false, "Internal - the plant and bud colours have been swapped over once");
            // One-off: swap the colours already chosen over, plant <-> buds.
            if (!_swapped.Value)
            {
                (_treeHex.Value, _budHex.Value) = (_budHex.Value, _treeHex.Value);
                _swapped.Value = true;
                prefs.SaveToFile(false);
            }
            _tree = Parse(_treeHex.Value, DefaultTree);
            _bud = Parse(_budHex.Value, DefaultBud);
        }

        private static Color Parse(string hex, Color fallback) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;

        public static void Clear() { _mats.Clear(); if (Visible) Close(); }

        // Called for each game material on the plant: a recolourable copy of it.
        public static Material Recolourable(Material src)
        {
            string n = src.name.ToLowerInvariant();
            bool plantBody = n.Contains("_plant_");                  // GranddaddyPurple_plant_leafmat / _stemmat
            float shade = plantBody ? (n.Contains("stem") ? 0.8f : 1f)
                        : n.Contains("main") ? 1.15f                    // bud tops, lightest
                        : n.Contains("secondary") ? 1f
                        : n.Contains("stem") ? 0.75f
                        : 0.9f;                                         // sugar leaves round the buds
            var m = new Material(src) { name = src.name + " (recolourable)" };
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
            _mats.Add((m, !plantBody, shade));
            Paint(m, !plantBody, shade);
            return m;
        }

        private static void Paint(Material m, bool bud, float shade)
        {
            var c = bud ? _bud : _tree;
            c = shade >= 1f ? Color.Lerp(c, Color.white, shade - 1f) : c * shade;
            c.a = 1f;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private static void Repaint()
        {
            foreach (var (m, bud, shade) in _mats)
                if (m != null) Paint(m, bud, shade);
        }

        // "E  Change plant colours" on the trunk.
        public static void AddInteraction(Transform plant, float height)
        {
            var go = new GameObject("ColourMenu");
            go.transform.SetParent(plant, false);
            _menuAt = go.transform;
            go.layer = InteractionLayer();
            var col = go.AddComponent<CapsuleCollider>();
            col.isTrigger = true;                       // found by the E raycast, but doesn't block you
            col.radius = 1.1f;
            col.height = height;
            col.center = Vector3.up * (height * 0.5f);
            var io = go.AddComponent<InteractableObject>();
            io.SetMessage("Change plant colours");
            io.MaxInteractionRange = 5f;
            io.onInteractStart.AddListener((UnityAction)(System.Action)Open);
        }

        // A layer the game's E raycast looks at: the one the game's own save intercom uses.
        public static int InteractionLayer()
        {
            foreach (var sp in Resources.FindObjectsOfTypeAll<SavePoint>())
            {
                if (sp == null || sp.IntObj == null || !sp.gameObject.scene.IsValid()) continue;
                var c = sp.IntObj.GetComponentInChildren<Collider>(true);
                return c != null ? c.gameObject.layer : sp.IntObj.gameObject.layer;
            }
            return 0;
        }

        private static void Open()
        {
            if (Visible) return;
            Visible = true;
            _treePicker.Set(_tree);
            _budPicker.Set(_bud);
            ColourWidgets.FocusedField = null;
            SetPlayerFree(true);
        }

        // Save: keep the colours and close the menu.
        private static void SaveAndClose()
        {
            _treeHex.Value = "#" + ColorUtility.ToHtmlStringRGB(_tree);
            _budHex.Value = "#" + ColorUtility.ToHtmlStringRGB(_bud);
            Core.Prefs.SaveToFile(false);
            Hud.Toast("Plant colours saved.");
            Close();
        }

        // Closing without saving (Esc) puts the last saved colours back.
        public static void Close()
        {
            if (!Visible) return;
            Visible = false;
            ColourWidgets.FocusedField = null;
            SetPlayerFree(false);
            _tree = Parse(_treeHex.Value, DefaultTree);
            _bud = Parse(_budHex.Value, DefaultBud);
            Repaint();
        }

        // Mouse on / look and walking off while the menu is up, the way the game's own menus do it.
        private static void SetPlayerFree(bool menuOpen)
        {
            try
            {
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null)
                {
                    if (menuOpen) { cam.AddActiveUIElement("PlantColours"); cam.FreeMouse(); cam.SetCanLook(false); }
                    else { cam.RemoveActiveUIElement("PlantColours"); cam.LockMouse(); cam.SetCanLook(true); }
                }
                var move = PlayerSingleton<PlayerMovement>.Instance;
                if (move != null) move.CanMove = !menuOpen;
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv != null) inv.SetInventoryEnabled(!menuOpen);
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't switch the mouse for the plant colour menu: " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!Visible) return;
            // Esc closes the menu without saving - unless you're typing in a hex box, where it just ends typing.
            if (Input.GetKeyDown(KeyCode.Escape) && ColourWidgets.FocusedField == null) { Close(); return; }
            // Walked away from the plant: the menu goes too.
            if (ColourWidgets.TooFarFrom(_menuAt, 8f)) Close();
        }

        private static Transform _menuAt;

        // Same panel as the Locker Customization mod: pinned top-left, one picker per part.
        private static readonly Rect WindowRect = new Rect(20f, 20f, 340f, 575f);   // tall enough for both pickers and the buttons
        private static readonly ColourWidgets.Picker _treePicker = new ColourWidgets.Picker("plantHex", "PelicanKey.leaf_icon.png");
        private static readonly ColourWidgets.Picker _budPicker = new ColourWidgets.Picker("budHex", "PelicanKey.bud_icon.png");

        public static void Draw()
        {
            if (!Visible) return;
            GUI.Window(848311, WindowRect, (GUI.WindowFunction)DrawWindow, "Plant Colour");
        }

        private static void DrawWindow(int id)
        {
            bool changed = ColourWidgets.DrawPicker("Plant Colour", _treePicker);
            GUILayout.Space(10f);
            changed |= ColourWidgets.DrawPicker("Bud Colour", _budPicker);
            if (changed)
            {
                _tree = _treePicker.Colour;
                _bud = _budPicker.Colour;
                Repaint();
            }

            GUILayout.Space(14f);
            if (GUILayout.Button("Restore to Default", GUILayout.Width(ColourWidgets.ContentWidth), GUILayout.Height(24f)))
            {
                _tree = DefaultTree; _bud = DefaultBud;
                _treePicker.Set(_tree); _budPicker.Set(_bud);
                Repaint();
            }
            GUILayout.Space(6f);
            if (GUILayout.Button("Save", GUILayout.Width(ColourWidgets.ContentWidth), GUILayout.Height(28f))) SaveAndClose();
            GUILayout.Label("Esc: close without saving");
        }
    }
}

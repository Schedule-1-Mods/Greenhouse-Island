using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PelicanKey
{
    // The colour picker layout the Locker Customization mod uses (ported from
    // LockerColorMod/LockerEditor.cs so all the mods look alike): a dark section title, a colour
    // spectrum bar (white / grey / black / brown caps, then the rainbow) and a brightness bar, each
    // with a round marker you drag, a colour preview square and a #hex box you can type or paste into.
    //
    // Same stripped-IMGUI workarounds as there: GUI.DrawTexture and GUI.DoTextField throw
    // "Method unstripping failed" in this game's IL2CPP build, so bars are drawn as styled boxes
    // and the text box is hand-rolled.
    internal static class ColourWidgets
    {
        public const float ContentWidth = 300f;
        private const int GradientSteps = 64;

        // Which hex box has the keyboard (null = none).
        public static string FocusedField;

        // For closing a menu once you've walked away from the thing it belongs to: is the
        // player more than `range` metres (along the ground) from it?
        public static bool TooFarFrom(Transform thing, float range)
        {
            if (thing == null) return true;
            try
            {
                var player = Il2CppScheduleOne.PlayerScripts.Player.Local;
                var at = player != null ? player.transform.position : (Camera.main != null ? Camera.main.transform.position : thing.position);
                var d = at - thing.position; d.y = 0f;
                return d.magnitude > range;
            }
            catch { return false; }
        }
        private static string _selectAllField;

        private static Texture2D _spectrumTexture, _brightnessTexture, _circleTexture, _solidTexture;
        private static GUIStyle _circleStyle, _solidStyle, _headerStyle, _sectionTitleTextStyle, _brightnessPercentStyle, _inputBoxStyle;
        private static bool _draggableBarSupported = true;

        // One colour being edited: its spectrum position, brightness and hex text.
        public class Picker
        {
            public float ColourT, BrightnessT = 1f;
            public string Hex = "#FFFFFF";
            public readonly string Id;
            // Embedded PNG shown, in the colour, as the preview (a plain square if it won't load).
            public readonly string IconResource;
            public GUIStyle IconStyle;
            public Picker(string id, string iconResource = null) { Id = id; IconResource = iconResource; }
            public Color Colour => ComposeColour(ColourT, BrightnessT);
            public void Set(Color c) { DecomposeColour(c, out ColourT, out BrightnessT); Hex = ColourToHex(c); }
        }

        // The whole block for one colour, as in the locker panel. Returns true if it changed.
        public static bool DrawPicker(string title, Picker p)
        {
            var before = p.Colour;
            DrawSectionTitle(title);

            GUILayout.Label("Colour");
            p.ColourT = DrawDraggableGradientBar(GetSpectrumTexture(), p.ColourT, SpectrumTToColour);

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Brightness");
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{Mathf.RoundToInt(p.BrightnessT * 100f)}%", GetBrightnessPercentStyle());
            GUILayout.EndHorizontal();
            p.BrightnessT = DrawDraggableGradientBar(GetBrightnessTexture(), p.BrightnessT, v => Color.Lerp(Color.black, Color.white, v));

            GUILayout.Space(6f);
            Color colour = p.Colour;
            string liveHex = ColourToHex(colour);

            GUILayout.BeginHorizontal();
            GUILayout.Space(40f);
            DrawShapePreview(GetIconStyle(p), colour, 70f, 70f);
            GUILayout.BeginVertical();
            GUILayout.Space(24f);
            string typed = DrawEditableField(p.Id, p.Hex, 7, 90f);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            if (typed != p.Hex)
            {
                p.Hex = typed;
                if (TryParseHex(p.Hex, out Color parsed)) DecomposeColour(parsed, out p.ColourT, out p.BrightnessT);
            }
            else if (FocusedField != p.Id && p.Hex != liveHex)
            {
                p.Hex = liveHex;
            }
            return p.Colour != before;
        }

        private static void DrawSectionTitle(string title)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
            GUILayout.BeginVertical(GetHeaderStyle(), GUILayout.Width(ContentWidth));
            GUI.color = previous;
            GUILayout.Label(title.ToUpperInvariant(), GetSectionTitleTextStyle());
            GUILayout.EndVertical();
        }

        private static GUIStyle GetSectionTitleTextStyle()
        {
            if (_sectionTitleTextStyle == null)
            {
                _sectionTitleTextStyle = new GUIStyle(GUI.skin.label);
                _sectionTitleTextStyle.fontSize = 16;
                _sectionTitleTextStyle.fontStyle = FontStyle.Bold;
                _sectionTitleTextStyle.alignment = TextAnchor.MiddleCenter;
                _sectionTitleTextStyle.normal.textColor = Color.white;
            }
            return _sectionTitleTextStyle;
        }

        private static GUIStyle GetBrightnessPercentStyle()
        {
            if (_brightnessPercentStyle == null)
            {
                _brightnessPercentStyle = new GUIStyle(GUI.skin.label);
                _brightnessPercentStyle.fontStyle = FontStyle.Bold;
                _brightnessPercentStyle.normal.textColor = new Color(0.95f, 0.75f, 0.25f);
            }
            return _brightnessPercentStyle;
        }

        private static GUIStyle GetHeaderStyle()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle();
                _headerStyle.padding = new RectOffset(6, 6, 4, 4);
                _headerStyle.normal.background = GetSolidTexture();
            }
            return _headerStyle;
        }

        private static GUIStyle GetInputBoxStyle()
        {
            if (_inputBoxStyle == null)
            {
                _inputBoxStyle = new GUIStyle(GUI.skin.textField);
                _inputBoxStyle.normal.textColor = Color.white;
                _inputBoxStyle.hover.textColor = Color.white;
                _inputBoxStyle.active.textColor = Color.white;
                _inputBoxStyle.focused.textColor = Color.white;
            }
            return _inputBoxStyle;
        }

        private static Rect DrawGradientBar(Texture2D texture)
        {
            const float height = 12f;
            Rect r = GUILayoutUtility.GetRect(ContentWidth, height, GUILayout.Width(ContentWidth), GUILayout.Height(height));
            GUIStyle style = new GUIStyle();
            style.normal.background = texture;
            GUI.Box(r, GUIContent.none, style);
            return r;
        }

        private static void DrawColourMarker(Rect barRect, float t, Color tint)
        {
            const float size = 16f;
            float cx = barRect.x + t * barRect.width;
            float cy = barRect.y + barRect.height / 2f;
            Rect circleRect = new Rect(cx - size / 2f, cy - size / 2f, size, size);
            Color previous = GUI.color;
            GUI.color = tint;
            GUI.Box(circleRect, GUIContent.none, GetCircleStyle());
            GUI.color = previous;
        }

        private static float DrawDraggableGradientBar(Texture2D texture, float t, System.Func<float, Color> tintAt)
        {
            Rect barRect = DrawGradientBar(texture);
            if (_draggableBarSupported)
            {
                try
                {
                    const float handleSize = 16f;
                    Rect hitRect = new Rect(barRect.x, barRect.y + barRect.height / 2f - handleSize / 2f, barRect.width, handleSize);
                    int controlId = GUIUtility.GetControlID(FocusType.Passive);
                    Event e = Event.current;
                    if (e.type == EventType.MouseDown && hitRect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = controlId;
                        t = Mathf.Clamp01((e.mousePosition.x - barRect.x) / barRect.width);
                        e.Use();
                    }
                    else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == controlId)
                    {
                        t = Mathf.Clamp01((e.mousePosition.x - barRect.x) / barRect.width);
                        e.Use();
                    }
                    else if (e.type == EventType.MouseUp && GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    DrawColourMarker(barRect, t, tintAt(t));
                    return t;
                }
                catch (System.Exception ex)
                {
                    Core.Log.Msg($"Draggable gradient bar unsupported, falling back to a plain slider: {ex.Message}");
                    _draggableBarSupported = false;
                }
            }
            DrawColourMarker(barRect, t, tintAt(t));
            return GUILayout.HorizontalSlider(t, 0f, 1f, GUILayout.Width(ContentWidth));
        }

        private static Texture2D GetCircleTexture()
        {
            if (_circleTexture != null) return _circleTexture;
            const int size = 20;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float radius = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - radius + 0.5f, dy = y - radius + 0.5f;
                tex.SetPixel(x, y, dx * dx + dy * dy <= radius * radius ? Color.white : new Color(0f, 0f, 0f, 0f));
            }
            tex.Apply();
            return _circleTexture = tex;
        }

        private static GUIStyle GetCircleStyle()
        {
            if (_circleStyle == null)
            {
                _circleStyle = new GUIStyle();
                _circleStyle.normal.background = GetCircleTexture();
            }
            return _circleStyle;
        }

        private static void DrawShapePreview(GUIStyle style, Color colour, float width, float height)
        {
            Rect r = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
            Color previous = GUI.color;
            GUI.color = colour;
            GUI.Box(r, GUIContent.none, style);
            GUI.color = previous;
        }

        private static Texture2D GetSolidTexture()
        {
            if (_solidTexture != null) return _solidTexture;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return _solidTexture = tex;
        }

        private static GUIStyle GetSolidStyle()
        {
            if (_solidStyle == null)
            {
                _solidStyle = new GUIStyle();
                _solidStyle.normal.background = GetSolidTexture();
            }
            return _solidStyle;
        }

        // The picker's icon. GUI tints by multiplying with GUI.color, so the icons are white where
        // they take the colour, black for their outlines and transparent round the outside.
        // (ImageConversion.LoadImage may be stripped in this build, so it's tried once, guarded.)
        private static GUIStyle GetIconStyle(Picker p)
        {
            if (p.IconStyle != null) return p.IconStyle;
            Texture2D tex = null;
            if (p.IconResource != null)
            {
                try
                {
                    using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(p.IconResource);
                    if (stream != null)
                    {
                        var bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(tex, bytes)) tex = null;
                    }
                }
                catch (System.Exception e)
                {
                    Core.Log.Msg($"Couldn't load the '{p.IconResource}' icon, using a plain square: {e.Message}");
                    tex = null;
                }
            }
            p.IconStyle = new GUIStyle();
            p.IconStyle.normal.background = tex != null ? tex : GetSolidTexture();
            return p.IconStyle;
        }

        private static Texture2D GetSpectrumTexture() => _spectrumTexture != null ? _spectrumTexture : _spectrumTexture = BuildGradient(SpectrumTToColour);
        private static Texture2D GetBrightnessTexture() => _brightnessTexture != null ? _brightnessTexture : _brightnessTexture = BuildGradient(t => Color.Lerp(Color.black, Color.white, t));

        private static Texture2D BuildGradient(System.Func<float, Color> colourAt)
        {
            var tex = new Texture2D(GradientSteps, 1);
            for (int x = 0; x < GradientSteps; x++) tex.SetPixel(x, 0, colourAt((float)x / (GradientSteps - 1)));
            tex.Apply();
            return tex;
        }

        private const float WhiteCapWidth = 0.06f, GreyCapWidth = 0.06f, BlackCapWidth = 0.06f, BrownCapWidth = 0.06f;
        private const float CapsWidth = WhiteCapWidth + GreyCapWidth + BlackCapWidth + BrownCapWidth;
        private static readonly Color GreyColour = new Color32(141, 141, 141, 255);
        private static readonly Color BrownColour = new Color32(114, 75, 53, 255);

        private static Color SpectrumTToColour(float t)
        {
            if (t <= WhiteCapWidth) return Color.white;
            if (t <= WhiteCapWidth + GreyCapWidth) return GreyColour;
            if (t <= WhiteCapWidth + GreyCapWidth + BlackCapWidth) return Color.black;
            if (t <= CapsWidth) return BrownColour;
            return Color.HSVToRGB((t - CapsWidth) / (1f - CapsWidth), 1f, 1f);
        }

        private static float ColourToSpectrumT(Color colour)
        {
            float max = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));
            float min = Mathf.Min(colour.r, Mathf.Min(colour.g, colour.b));
            float saturation = max <= 0f ? 0f : (max - min) / max;
            if (saturation < 0.08f)
            {
                float lightness = (max + min) / 2f;
                if (lightness > 0.85f) return WhiteCapWidth / 2f;
                if (lightness < 0.15f) return WhiteCapWidth + GreyCapWidth + BlackCapWidth / 2f;
                return WhiteCapWidth + GreyCapWidth / 2f;
            }
            float brownDistance = Mathf.Abs(colour.r - BrownColour.r) + Mathf.Abs(colour.g - BrownColour.g) + Mathf.Abs(colour.b - BrownColour.b);
            if (brownDistance < 0.3f) return WhiteCapWidth + GreyCapWidth + BlackCapWidth + BrownCapWidth / 2f;
            Color.RGBToHSV(colour, out float hue, out _, out _);
            return CapsWidth + hue * (1f - CapsWidth);
        }

        public static Color ComposeColour(float colourT, float brightnessT)
        {
            Color b = SpectrumTToColour(colourT);
            return new Color(b.r * brightnessT, b.g * brightnessT, b.b * brightnessT, 1f);
        }

        public static void DecomposeColour(Color colour, out float colourT, out float brightnessT)
        {
            float value = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));
            if (value <= 0.0001f)
            {
                colourT = ColourToSpectrumT(Color.black);
                brightnessT = 1f;
                return;
            }
            colourT = ColourToSpectrumT(new Color(colour.r / value, colour.g / value, colour.b / value));
            brightnessT = Mathf.Clamp01(value);
        }

        public static string ColourToHex(Color colour)
        {
            Color32 c = colour;
            return $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        }

        public static bool TryParseHex(string text, out Color colour)
        {
            colour = Color.white;
            string t = text.TrimStart('#');
            if (t.Length != 6) return false;
            if (!byte.TryParse(t.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
                !byte.TryParse(t.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
                !byte.TryParse(t.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                return false;
            colour = new Color32(r, g, b, 255);
            return true;
        }

        // Windows clipboard straight through user32/kernel32 (GUIUtility.systemCopyBuffer is stripped).
        private const uint ClipboardFormatUnicodeText = 13;
        private const uint GlobalMemMoveableZeroed = 0x0042;
        [DllImport("user32.dll")] private static extern bool OpenClipboard(System.IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern System.IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] private static extern System.IntPtr SetClipboardData(uint uFormat, System.IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern System.IntPtr GlobalAlloc(uint uFlags, System.UIntPtr dwBytes);
        [DllImport("kernel32.dll")] private static extern System.IntPtr GlobalLock(System.IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(System.IntPtr hMem);

        private static void TrySetClipboard(string text)
        {
            try
            {
                if (!OpenClipboard(System.IntPtr.Zero)) return;
                try
                {
                    EmptyClipboard();
                    var hGlobal = GlobalAlloc(GlobalMemMoveableZeroed, (System.UIntPtr)((text.Length + 1) * 2));
                    if (hGlobal == System.IntPtr.Zero) return;
                    var target = GlobalLock(hGlobal);
                    if (target == System.IntPtr.Zero) return;
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    GlobalUnlock(hGlobal);
                    SetClipboardData(ClipboardFormatUnicodeText, hGlobal);
                }
                finally { CloseClipboard(); }
            }
            catch { }
        }

        private static string TryGetClipboard()
        {
            try
            {
                if (!OpenClipboard(System.IntPtr.Zero)) return null;
                try
                {
                    var handle = GetClipboardData(ClipboardFormatUnicodeText);
                    if (handle == System.IntPtr.Zero) return null;
                    var pointer = GlobalLock(handle);
                    if (pointer == System.IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(pointer); } finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }
            catch { return null; }
        }

        // Minimal self-drawn text box (GUILayout.TextField needs the stripped GUI.DoTextField).
        private static string DrawEditableField(string fieldId, string value, int maxLength, float width)
        {
            bool isFocused = FocusedField == fieldId;
            bool isSelectedAll = isFocused && _selectAllField == fieldId;
            string display = isFocused ? value + "|" : value;

            Color previousBg = GUI.backgroundColor;
            if (isSelectedAll) GUI.backgroundColor = new Color(0.3f, 0.5f, 1f, 1f);
            bool clicked = GUILayout.Button(display, GetInputBoxStyle(), GUILayout.Width(width), GUILayout.Height(20f));
            GUI.backgroundColor = previousBg;

            if (clicked)
            {
                bool wasFocused = isFocused;
                FocusedField = wasFocused ? null : fieldId;
                isFocused = !wasFocused;
                _selectAllField = isFocused ? fieldId : null;
            }
            if (!isFocused) return value;

            Event evt = Event.current;
            if (evt.type != EventType.KeyDown) return value;
            bool selectedAll = _selectAllField == fieldId;

            if (evt.control && evt.keyCode == KeyCode.A) _selectAllField = fieldId;
            else if (evt.control && evt.keyCode == KeyCode.C) TrySetClipboard(value);
            else if (evt.control && evt.keyCode == KeyCode.V)
            {
                string pasted = TryGetClipboard();
                if (!string.IsNullOrEmpty(pasted)) value = pasted.Length > maxLength ? pasted.Substring(0, maxLength) : pasted;
                _selectAllField = null;
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Escape)
            {
                FocusedField = null;
                _selectAllField = null;
            }
            else if (evt.keyCode == KeyCode.Backspace)
            {
                value = selectedAll ? string.Empty : (value.Length > 0 ? value.Substring(0, value.Length - 1) : value);
                _selectAllField = null;
            }
            else if (evt.character != '\0' && !char.IsControl(evt.character))
            {
                value = selectedAll ? string.Empty : value;
                if (value.Length < maxLength) value += evt.character;
                _selectAllField = null;
            }
            evt.Use();
            return value;
        }
    }
}

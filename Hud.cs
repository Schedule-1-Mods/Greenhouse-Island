using UnityEngine;

namespace PelicanKey
{
    // A small on-screen message box, e.g. "Locked" at the door or map-placement feedback.
    internal static class Hud
    {
        private static string _text;
        private static float _until;
        private static GUIStyle _style;

        public static void Toast(string text, float seconds = 4f)
        {
            _text = text;
            _until = Time.time + seconds;
        }

        public static void Draw()
        {
            if (_text == null || Time.time > _until) return;
            _style ??= new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Box(new Rect(Screen.width * 0.5f - 300f, Screen.height - 180f, 600f, 44f), _text, _style);
        }
    }
}

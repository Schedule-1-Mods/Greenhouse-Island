using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Property;

namespace PelicanKey
{
    // Adds "Greenhouse Island ($0)" to Ray's list of properties for sale.
    //
    // Each choice in that list is labelled with a property code, and all of them lead to the same
    // "confirm purchase" node. So the new choice borrows another property's choice GUID (which
    // decides where the conversation goes next) with our own label (which decides which
    // property Ray sells) - his normal buying flow then does the rest.
    [HarmonyPatch(typeof(DialogueHandler), nameof(DialogueHandler.ShowNode))]
    internal static class RayPatch
    {
        private static void Prefix(DialogueHandler __instance, DialogueNodeData node)
        {
            try
            {
                if (node == null || node.choices == null || JungleProperty.Property == null) return;
                if (__instance.TryCast<DialogueHandler_EstateAgent>() == null) return;

                var choices = node.choices;
                DialogueChoiceData template = null;
                int insertAt = -1;
                for (int i = 0; i < choices.Length; i++)
                {
                    var c = choices[i];
                    if (c == null || string.IsNullOrEmpty(c.ChoiceLabel)) continue;
                    if (c.ChoiceLabel.ToLowerInvariant() == JungleProperty.Code) return; // already added
                    if (IsPlainProperty(c.ChoiceLabel))
                    {
                        template ??= c;
                        insertAt = i + 1;
                    }
                }
                if (template == null) return;

                var ours = new DialogueChoiceData
                {
                    Guid = template.Guid,
                    ChoiceText = JungleProperty.Name + " (<PRICE>)",
                    ChoiceLabel = JungleProperty.Code,
                    ShowWorldspaceDialogue = template.ShowWorldspaceDialogue,
                };

                var grown = new Il2CppReferenceArray<DialogueChoiceData>(choices.Length + 1);
                for (int i = 0, j = 0; i < grown.Length; i++)
                    grown[i] = i == insertAt ? ours : choices[j++];
                node.choices = grown;
                Core.Log.Msg("Added Greenhouse Island to Ray's property list.");
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("Couldn't add the island to Ray's list: " + e.Message);
            }
        }

        // A property (not a business - those go through a different confirm node).
        private static bool IsPlainProperty(string code)
        {
            foreach (var p in Property.Properties)
            {
                if (p == null || p.PropertyCode == null) continue;
                if (p.PropertyCode.ToLowerInvariant() != code.ToLowerInvariant()) continue;
                if (p.PropertyCode == JungleProperty.Code) return false;
                return p.TryCast<Business>() == null;
            }
            return false;
        }
    }
}

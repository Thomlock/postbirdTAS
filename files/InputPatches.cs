using HarmonyLib;
using UnityEngine;

namespace PostbirdTAS
{
    /// <summary>
    /// On patche la classe Input elle-meme plutot que de chercher la classe
    /// "InputManager" interne au jeu : c'est plus robuste (ca marche quelle que
    /// soit la facon dont le jeu lit ses entrees) et ca n'exige pas de connaitre
    /// les noms exacts des champs prives du jeu.
    ///
    /// IMPORTANT : les noms d'axes ("Horizontal", "Vertical", "Jump"...) sont les
    /// noms par defaut d'Unity. Verifie les vrais noms configures par le jeu dans
    /// Edit > Project Settings > Input Manager si tu as le projet, ou en lancant
    /// une fois en mode "enregistrement" (F9) et en regardant les logs BepInEx
    /// pour voir quels axes sont effectivement interroges.
    /// </summary>
    [HarmonyPatch]
    internal static class InputPatches
    {
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
        [HarmonyPrefix]
        private static bool GetAxis_Prefix(string axisName, ref float __result)
        {
            var tas = TasController.Instance;
            if (tas == null) return true;

            if (tas.IsPlayingBack && tas.HasCurrentFrame())
            {
                var frame = tas.GetCurrentFrame();
                __result = axisName == "Horizontal" ? frame.Horizontal
                         : axisName == "Vertical" ? frame.Vertical
                         : 0f;
                return false; // on saute l'appel original
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw))]
        [HarmonyPrefix]
        private static bool GetAxisRaw_Prefix(string axisName, ref float __result)
            => GetAxis_Prefix(axisName, ref __result);

        [HarmonyPatch(typeof(Input), nameof(Input.GetButton))]
        [HarmonyPrefix]
        private static bool GetButton_Prefix(string buttonName, ref bool __result)
        {
            var tas = TasController.Instance;
            if (tas == null) return true;

            if (tas.IsPlayingBack && tas.HasCurrentFrame())
            {
                var frame = tas.GetCurrentFrame();
                __result = buttonName switch
                {
                    "Jump" => frame.Jump,
                    "Interact" => frame.Interact,
                    "Brake" => frame.Brake,
                    _ => false,
                };
                return false;
            }
            return true;
        }

        // En mode enregistrement, on laisse l'appel original s'executer (return true)
        // et on logge le resultat reel pour construire la movie frame par frame.
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
        [HarmonyPostfix]
        private static void GetAxis_Postfix(string axisName, float __result)
        {
            TasController.Instance?.RecordAxisSample(axisName, __result);
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetButton))]
        [HarmonyPostfix]
        private static void GetButton_Postfix(string buttonName, bool __result)
        {
            TasController.Instance?.RecordButtonSample(buttonName, __result);
        }
    }
}

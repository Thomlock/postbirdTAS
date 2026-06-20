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
    ///
    /// Support manette : Unity's Input Manager melange clavier/manette sous les
    /// memes noms d'axe/bouton ("Horizontal", "Jump", ...), donc en theorie rien
    /// de special n'est requis ici. En pratique, deux ecueils frequents avec une
    /// manette :
    /// 1) Beaucoup de jeux lisent un saut/une action au pad via GetButtonDown
    ///    (declenchement ponctuel) plutot que GetButton (maintenu). On patche
    ///    donc aussi GetButtonDown/GetButtonUp, sinon ces actions sont invisibles
    ///    a l'enregistrement et ignorees a la lecture.
    /// 2) Les axes manette passent par le lissage Sensitivity/Gravity du Input
    ///    Manager, qui se base sur le temps reel ecoule frame a frame. En mode
    ///    pause/avance frame, le temps est fige entre deux pas (un seul Update
    ///    tres court est laisse passer a chaque F2) : ce lissage n'a pas le temps
    ///    de monter normalement et le stick peut sembler repondre mollement /
    ///    de travers specifiquement en frame par frame. On enregistre donc
    ///    toujours la valeur BRUTE (GetAxisRaw), qui ignore ce lissage et donne
    ///    la deflexion reelle du stick a l'instant T - de toute facon ce qu'on
    ///    veut pour un enregistrement TAS deterministe.
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

            if (tas.IsFrameAdvanceMode)
            {
                // C'est ici que se jouait le bug "acceleration manette" :
                // GetAxis() applique le lissage Sensitivity/Gravity d'Unity,
                // qui se base sur le temps reel ecoule. Sur clavier ce lissage
                // est configure par defaut tres rapide (Sensitivity tres
                // elevee + Snap), donc invisible meme sur un seul tick fige.
                // Sur manette, il est volontairement plus progressif pour un
                // ressenti analogique, et n'a pas le temps de monter entre
                // deux pas geles : le jeu recevait une deflexion quasi nulle
                // a chaque frame, donc peu/pas d'acceleration. On bypasse ce
                // lissage en redirigeant systematiquement le jeu vers la
                // valeur BRUTE (GetAxisRaw, jamais lissee par Unity, et que
                // GetAxisRaw_Prefix laisse passer tel quel hors lecture de
                // movie - pas de recursion ici).
                __result = Input.GetAxisRaw(axisName);
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw))]
        [HarmonyPrefix]
        private static bool GetAxisRaw_Prefix(string axisName, ref float __result)
        {
            var tas = TasController.Instance;
            if (tas == null) return true;

            if (tas.IsPlayingBack && tas.HasCurrentFrame())
            {
                var frame = tas.GetCurrentFrame();
                __result = axisName == "Horizontal" ? frame.Horizontal
                         : axisName == "Vertical" ? frame.Vertical
                         : 0f;
                return false;
            }

            // GetAxisRaw n'est de toute facon jamais lissee par Unity : rien
            // a corriger ici hors lecture de movie, on laisse l'appel
            // original (c'est aussi ce que GetAxis_Prefix appelle ci-dessus
            // pour bypasser le lissage, donc important que cette methode ne
            // redirige plus vers GetAxis_Prefix comme avant, sous peine de
            // recursion infinie).
            return true;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetButton))]
        [HarmonyPrefix]
        private static bool GetButton_Prefix(string buttonName, ref bool __result)
            => GetButtonLike_Prefix(buttonName, ref __result);

        // Beaucoup de jeux lisent les actions au pad (saut, interaction...) via
        // GetButtonDown plutot que GetButton. Sans ce patch, ces actions ne
        // seraient ni enregistrees ni rejouees pour ce genre d'entree.
        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown))]
        [HarmonyPrefix]
        private static bool GetButtonDown_Prefix(string buttonName, ref bool __result)
            => GetButtonLike_Prefix(buttonName, ref __result);

        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonUp))]
        [HarmonyPrefix]
        private static bool GetButtonUp_Prefix(string buttonName, ref bool __result)
        {
            var tas = TasController.Instance;
            if (tas == null) return true;

            if (tas.IsPlayingBack && tas.HasCurrentFrame())
            {
                // Chaque frame enregistree correspond a exactement un pas moteur
                // (cf. StepOneFrameCoroutine), donc "relache ce pas-ci" equivaut
                // simplement a "le bouton n'est pas enfonce sur cette frame".
                var frame = tas.GetCurrentFrame();
                __result = !ButtonStateFor(buttonName, frame);
                return false;
            }
            return true;
        }

        private static bool GetButtonLike_Prefix(string buttonName, ref bool __result)
        {
            var tas = TasController.Instance;
            if (tas == null) return true;

            if (tas.IsPlayingBack && tas.HasCurrentFrame())
            {
                __result = ButtonStateFor(buttonName, tas.GetCurrentFrame());
                return false;
            }
            return true;
        }

        private static bool ButtonStateFor(string buttonName, InputFrame frame) => buttonName switch
        {
            "Jump" => frame.Jump,
            "Interact" => frame.Interact,
            "Brake" => frame.Brake,
            _ => false,
        };

        // En mode enregistrement, on laisse l'appel original s'executer (return true)
        // et on logge le resultat pour construire la movie frame par frame. Pour
        // les axes, on relit toujours la valeur brute (GetAxisRaw) plutot que de
        // faire confiance a __result, qui peut etre lissee par Unity pour les
        // axes manette (cf. commentaire de classe ci-dessus).
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
        [HarmonyPostfix]
        private static void GetAxis_Postfix(string axisName, float __result) => RecordAxis(axisName);

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw))]
        [HarmonyPostfix]
        private static void GetAxisRaw_Postfix(string axisName, float __result) => RecordAxis(axisName);

        private static void RecordAxis(string axisName)
        {
            var tas = TasController.Instance;
            if (tas == null || !tas.IsRecording) return;
            tas.RecordAxisSample(axisName, Input.GetAxisRaw(axisName));
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetButton))]
        [HarmonyPostfix]
        private static void GetButton_Postfix(string buttonName, bool __result) => RecordButton(buttonName, __result);

        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown))]
        [HarmonyPostfix]
        private static void GetButtonDown_Postfix(string buttonName, bool __result) => RecordButton(buttonName, __result);

        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonUp))]
        [HarmonyPostfix]
        private static void GetButtonUp_Postfix(string buttonName, bool __result)
        {
            // Un GetButtonUp vrai signifie "relache sur ce pas" : ca ne doit pas
            // ecraser un Jump/Interact/Brake deja mis a true par GetButton(Down)
            // sur le meme pas (ex: un script qui teste GetButtonUp pour une autre
            // action que celle qui vient d'etre enfoncee). On ne touche au champ
            // que lorsque le bouton est effectivement relache.
            if (__result)
                RecordButton(buttonName, false);
        }

        private static void RecordButton(string buttonName, bool value)
        {
            var tas = TasController.Instance;
            if (tas == null || !tas.IsRecording) return;
            tas.RecordButtonSample(buttonName, value);
        }
    }
}
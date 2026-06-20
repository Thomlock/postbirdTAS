using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace PostbirdTAS
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.tasmod.postbirdinprovence";
        public const string PluginName = "PostbirdTAS";
        public const string PluginVersion = "0.1.0";

        public override void Load()
        {
            Log.LogInfo($"{PluginName} v{PluginVersion} - chargement...");

            // IL2CPP exige d'enregistrer tout nouveau type MonoBehaviour avant de
            // pouvoir l'attacher à un GameObject.
            ClassInjector.RegisterTypeInIl2Cpp<TasController>();

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            var host = new GameObject("PostbirdTAS_Controller");
            host.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(host);
            host.AddComponent<TasController>();

            Log.LogInfo($"{PluginName} pret. F1 = pause/avance frame, F2 = avancer d'une frame, "
                      + "F5 = save state, F7 = load state, F9 = enregistrer un movie, F10 = rejouer.");
        }
    }
}

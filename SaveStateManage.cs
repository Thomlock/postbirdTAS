using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace PostbirdTAS
{
    public struct StateSnapshot
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public Dictionary<string, object> GameSpecificFields;
    }

    /// <summary>
    /// Le coeur d'un savestate "TAS" pour un jeu de mouvement (velo) est la
    /// position/rotation/vitesse du joueur : c'est generique et ne demande
    /// aucune connaissance des classes internes du jeu.
    ///
    /// On tente en plus, en best-effort, de sauvegarder quelques champs du
    /// composant MainPlayerController (confirme present dans le binaire) sans
    /// avoir besoin de connaitre son namespace exact a la compilation : on le
    /// retrouve par reflexion via son nom de type, puis on lit/ecrit les champs
    /// par nom egalement. Si un champ n'existe pas ou a un type inattendu, on
    /// l'ignore simplement plutot que de planter.
    /// </summary>
    public class SaveStateManager
    {
        private readonly Dictionary<int, StateSnapshot> slots = new();
        private Rigidbody playerRigidbody;
        private Component mainPlayerController;


        private readonly LinkedList<StateSnapshot> rewindBuffer = new();   // <-- celle-ci manque
        private const int MaxRewindFrames = 600;    
        public void CaptureRewindFrame()
{
    EnsurePlayerFound();
    if (playerRigidbody == null) return;

    rewindBuffer.AddLast(new StateSnapshot
    {
        Position = playerRigidbody.transform.position,
        Rotation = playerRigidbody.transform.rotation,
        Velocity = playerRigidbody.velocity,
        AngularVelocity = playerRigidbody.angularVelocity,
        GameSpecificFields = SnapshotGameSpecificFields(),
    });

    if (rewindBuffer.Count > MaxRewindFrames)
        rewindBuffer.RemoveFirst();
}

public bool RewindOneStep()
{
    if (rewindBuffer.Count == 0) return false;

    var snap = rewindBuffer.Last.Value;
    rewindBuffer.RemoveLast();

    EnsurePlayerFound();
    if (playerRigidbody == null) return false;

    playerRigidbody.transform.position = snap.Position;
    playerRigidbody.transform.rotation = snap.Rotation;
    playerRigidbody.velocity = snap.Velocity;
    playerRigidbody.angularVelocity = snap.AngularVelocity;
    RestoreGameSpecificFields(snap.GameSpecificFields);
    return true;
}

public bool HasRewindData => rewindBuffer.Count > 0;
        // Noms confirmes par extraction des chaines de global-metadata.dat.
        // A completer/corriger via dnSpy sur le Assembly-CSharp.dll genere par
        // BepInEx dans BepInEx/interop/ apres le premier lancement du jeu module.
        private static readonly string[] CandidateFieldNames =
        {
            "Speed", "MaxSpeed", "Acceleration", "IsPaused", "CurrentPackage"
        };

        public void EnsurePlayerFound()
        {
            if (playerRigidbody != null) return;

            // 1) Par tag, si le GameObject du joueur est tagge "Player" (a verifier
            //    dans l'editeur si tu as acces au projet, sinon laisser tomber ce essai).
            var tagged = GameObject.FindGameObjectWithTag("Player");
            if (tagged != null)
                playerRigidbody = tagged.GetComponent<Rigidbody>();

            // 2) Par type, en cherchant dynamiquement "MainPlayerController" parmi
            //    les assemblies chargees, sans dependre de son namespace exact.
            var playerType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(t => t.Name == "MainPlayerController");

            if (playerType != null)
            {
                var il2cppType = Il2CppInterop.Runtime.Il2CppType.From(playerType);
                if (UnityEngine.Object.FindObjectOfType(il2cppType) is Component found)
                {
                    mainPlayerController = found;
                    if (playerRigidbody == null)
                        playerRigidbody = found.GetComponent<Rigidbody>();
                }
            }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch { return Array.Empty<Type>(); }
        }

        public bool SaveState(int slot)
        {
            EnsurePlayerFound();
            if (playerRigidbody == null) return false;

            slots[slot] = new StateSnapshot
            {
                Position = playerRigidbody.transform.position,
                Rotation = playerRigidbody.transform.rotation,
                Velocity = playerRigidbody.velocity,
                AngularVelocity = playerRigidbody.angularVelocity,
                GameSpecificFields = SnapshotGameSpecificFields(),
            };
            return true;
        }

        public bool LoadState(int slot)
        {
            if (!slots.TryGetValue(slot, out var snap)) return false;
            EnsurePlayerFound();
            if (playerRigidbody == null) return false;

            playerRigidbody.transform.position = snap.Position;
            playerRigidbody.transform.rotation = snap.Rotation;
            playerRigidbody.velocity = snap.Velocity;
            playerRigidbody.angularVelocity = snap.AngularVelocity;

            RestoreGameSpecificFields(snap.GameSpecificFields);
            return true;
        }

        private Dictionary<string, object> SnapshotGameSpecificFields()
        {
            var values = new Dictionary<string, object>();
            if (mainPlayerController == null) return values;

            var type = mainPlayerController.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var name in CandidateFieldNames)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop != null && prop.CanRead) { values[name] = prop.GetValue(mainPlayerController); continue; }

                    var field = type.GetField(name, flags);
                    if (field != null) values[name] = field.GetValue(mainPlayerController);
                }
                catch
                {
                    // Champ absent / type incompatible : pas grave, la trajectoire
                    // physique (Position/Rotation/Velocity) reste sauvegardee.
                }
            }
            return values;
        }

        private void RestoreGameSpecificFields(Dictionary<string, object> values)
        {
            if (mainPlayerController == null || values == null) return;
            var type = mainPlayerController.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var kv in values)
            {
                try
                {
                    var prop = type.GetProperty(kv.Key, flags);
                    if (prop != null && prop.CanWrite) { prop.SetValue(mainPlayerController, kv.Value); continue; }

                    var field = type.GetField(kv.Key, flags);
                    field?.SetValue(mainPlayerController, kv.Value);
                }
                catch
                {
                    // idem
                }
            }
        }
    }
}

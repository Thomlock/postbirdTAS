using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace PostbirdTAS
{
    /// <summary>
    /// Composant unique attache a un GameObject persistant (cree dans Plugin.Load).
    /// Centralise tout l'etat du mod TAS : pause/avance frame, save states,
    /// enregistrement et lecture d'une movie d'inputs.
    /// </summary>
    public class TasController : MonoBehaviour
    {
        // Obligatoire pour tout MonoBehaviour injecte en IL2CPP.
        public TasController(IntPtr ptr) : base(ptr) { }

        public static TasController Instance { get; private set; }

        private bool seekInputActive;
        private string seekInputBuffer = "";
        private bool isFastForwarding;
        private StateSnapshot? seekAnchor; // etat au frame 0 de la lecture

        private float f2HoldTimer;
        private const float HoldThreshold = 0.3f;     // delai avant que la repetition demarre
        private const float AutoStepInterval = 1f / 5f; // 5 frames par seconde

        public bool IsEditMode { get; private set; }

        public bool IsFrameAdvanceMode { get; private set; }
        public bool IsRecording { get; private set; }
        public bool IsPlayingBack { get; private set; }

        private bool stepRequested;
        private int playbackIndex;

        private readonly SaveStateManager saveStates = new();
        private Movie currentMovie = new();
        private InputFrame pendingFrame; // accumule les echantillons de la frame en cours pendant l'enregistrement

        private static readonly string MoviePath =
            Path.Combine(Paths.GameRootPath, "PostbirdTAS_movie.tas");

        private void Awake()
        {
            Instance = this;
        }

        private bool stepping;

        private void Update()
        {
            HandleHotkeys();

            if (NativeKeys.IsDown(NativeKeys.VK_F4))
            {
                if (saveStates.RewindOneStep())
                    Time.timeScale = 0f; // fige pendant le rewind pour eviter que la physique "rejoue" par-dessus
            }

            if (IsFrameAdvanceMode)
            {
                // Tant qu'on n'est pas en train d'executer un pas, le jeu reste
                // completement fige (Update ET FixedUpdate suspendus par timeScale=0).
                // Ca marche quelle que soit la facon dont le jeu deplace le joueur
                // (FixedUpdate/Rigidbody ou Update/Time.deltaTime), contrairement a
                // un simple Physics.Simulate() manuel qui ne couvre que la physique.
                if (!stepping)
                {
                    Time.timeScale = 0f;
                }

                if (stepRequested && !stepping)
                {
                    stepRequested = false;
                    StartCoroutine(StepOneFrameCoroutine().WrapToIl2Cpp());
                }
            }
            else
            {
                Time.timeScale = 1f;
            }
        }

        // Laisse passer exactement une frame moteur a vitesse normale, puis
        // re-fige le jeu. C'est l'equivalent d'un "frame advance" generique.
        private System.Collections.IEnumerator StepOneFrameCoroutine()
        {
            stepping = true;
            Time.timeScale = 1f;

            yield return new WaitForFixedUpdate(); // garantit qu'un FixedUpdate s'execute
            yield return null;                      // puis laisse Update suivre aussi

            Time.timeScale = 0f;
            stepping = false;
            AdvanceRecordingOrPlayback();
        }

        private void HandleHotkeys()
        {
            if (IsFrameAdvanceMode)
            {
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F2))
                {
                    stepRequested = true;
                    f2HoldTimer = 0f;
                }
                else if (NativeKeys.IsDown(NativeKeys.VK_F2))
                {
                    f2HoldTimer += Time.unscaledDeltaTime; // unscaled car timeScale=0 en pause
                    if (f2HoldTimer >= HoldThreshold)
                    {
                        f2HoldTimer -= AutoStepInterval;
                        stepRequested = true;
                    }
                }
                else
                {
                    f2HoldTimer = 0f;
                }
            }

            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F4))
            {
                if (!IsPlayingBack || !IsFrameAdvanceMode)
                {
                    Debug.LogWarning("[PostbirdTAS] Le mode edition exige d'etre en lecture (F10) ET en pause/avance frame (F1).");
                }
                else
                {
                    IsEditMode = !IsEditMode;
                    Debug.Log(IsEditMode
                        ? $"[PostbirdTAS] Mode edition ON, frame {playbackIndex}."
                        : "[PostbirdTAS] Mode edition OFF.");
                }
            }

            if (IsEditMode && HasCurrentFrame())
            {
                var frame = currentMovie.Frames[playbackIndex];
                bool changed = false;

                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_J)) { frame.Jump = !frame.Jump; changed = true; }
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_I)) { frame.Interact = !frame.Interact; changed = true; }
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_B)) { frame.Brake = !frame.Brake; changed = true; }
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_LEFT))  { frame.Horizontal = Mathf.Clamp(frame.Horizontal - 0.1f, -1f, 1f); changed = true; }
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_RIGHT)) { frame.Horizontal = Mathf.Clamp(frame.Horizontal + 0.1f, -1f, 1f); changed = true; }
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_DOWN))  { frame.Vertical = Mathf.Clamp(frame.Vertical - 0.1f, -1f, 1f); changed = true; }
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_UP))    { frame.Vertical = Mathf.Clamp(frame.Vertical + 0.1f, -1f, 1f); changed = true; }

                if (changed)
                {
                    currentMovie.Frames[playbackIndex] = frame;
                    Debug.Log($"[PostbirdTAS] Frame {playbackIndex} editee : {frame}");
                }

                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_S))
                {
                    currentMovie.Save(MoviePath);
                    Debug.Log($"[PostbirdTAS] Movie sauvegardee sur disque ({MoviePath}).");
                }
            }
            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F3) && IsPlayingBack && !isFastForwarding)
            {
                seekInputActive = !seekInputActive;
                seekInputBuffer = "";
                Debug.Log(seekInputActive
                    ? "[PostbirdTAS] Saisie du numero de frame : tape les chiffres puis Entree."
                    : "[PostbirdTAS] Saisie annulee.");
            }

            if (seekInputActive)
            {
                for (int digit = 0; digit <= 9; digit++)
                {
                    if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_0 + digit))
                        seekInputBuffer += digit.ToString();
                }
                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_BACK) && seekInputBuffer.Length > 0)
                    seekInputBuffer = seekInputBuffer[..^1];

                if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_RETURN))
                {
                    seekInputActive = false;
                    if (int.TryParse(seekInputBuffer, out int target))
                        SeekToFrame(target);
                }
            }
            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F1))
            {
                IsFrameAdvanceMode = !IsFrameAdvanceMode;
                Debug.Log($"[PostbirdTAS] Mode avance frame: {IsFrameAdvanceMode}");
            }

            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F2) && IsFrameAdvanceMode)
            {
                stepRequested = true;
            }

            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F5))
            {
                bool ok = saveStates.SaveState(0);
                Debug.Log(ok ? "[PostbirdTAS] Save state slot 0 OK" : "[PostbirdTAS] Save state echoue (joueur introuvable)");
            }

            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F7))
            {
                bool ok = saveStates.LoadState(0);
                Debug.Log(ok ? "[PostbirdTAS] Load state slot 0 OK" : "[PostbirdTAS] Load state echoue (rien sauvegarde ?)");
            }

            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F9))
            {
                ToggleRecording();
            }

            if (NativeKeys.WasPressedThisFrame(NativeKeys.VK_F10))
            {
                TogglePlayback();
            }
        }

        public void SeekToFrame(int targetFrame)
        {
            if (!IsPlayingBack || currentMovie.Frames.Count == 0) return;
            targetFrame = Mathf.Clamp(targetFrame, 0, currentMovie.Frames.Count);

            // On ne peut avancer qu'en rejouant les inputs depuis le debut,
            // donc si la cible est avant la position courante, on recharge l'ancre.
            if (targetFrame < playbackIndex)
            {
                if (saveStates.LoadState(99))
                    playbackIndex = 0;
                else
                {
                    Debug.LogWarning("[PostbirdTAS] Pas d'ancre disponible, seek annule.");
                    return;
                }
            }

            StartCoroutine(FastForwardCoroutine(targetFrame).WrapToIl2Cpp());
        }

        private System.Collections.IEnumerator FastForwardCoroutine(int targetFrame)
        {
            isFastForwarding = true;
            IsFrameAdvanceMode = false; // on sort du mode pause manuel pendant le seek
            Time.timeScale = 20f;       // accelere la simulation; ajuste selon stabilite physique

            while (playbackIndex < targetFrame && IsPlayingBack)
            {
                yield return null;
                AdvanceRecordingOrPlayback();
            }

            Time.timeScale = 0f;
            IsFrameAdvanceMode = true; // on re-fige en mode frame-advance pour pouvoir inspecter/continuer
            isFastForwarding = false;
            Debug.Log($"[PostbirdTAS] Frame atteinte : {playbackIndex}");
        }

        private void ToggleRecording()
        {
            IsRecording = !IsRecording;
            if (IsRecording)
            {
                currentMovie = new Movie();
                Debug.Log("[PostbirdTAS] Enregistrement demarre.");
            }
            else
            {
                currentMovie.Save(MoviePath);
                Debug.Log($"[PostbirdTAS] Enregistrement arrete, {currentMovie.Frames.Count} frames sauvegardees dans {MoviePath}");
            }
        }

        private void TogglePlayback()
        {
            currentMovie = Movie.Load(MoviePath);
            playbackIndex = 0;
            IsPlayingBack = true;
            saveStates.SaveState(99); // slot reserve = "ancre frame 0" pour le seek
            Debug.Log($"[PostbirdTAS] Lecture demarree ({currentMovie.Frames.Count} frames).");
            if (!IsPlayingBack)
            {
                if (!File.Exists(MoviePath))
                {
                    Debug.LogWarning($"[PostbirdTAS] Aucune movie trouvee : {MoviePath}");
                    return;
                }
                currentMovie = Movie.Load(MoviePath);
                playbackIndex = 0;
                IsPlayingBack = true;
                Debug.Log($"[PostbirdTAS] Lecture demarree ({currentMovie.Frames.Count} frames).");
            }
            else
            {
                IsPlayingBack = false;
                Debug.Log("[PostbirdTAS] Lecture arretee.");
            }
        }

        // --- Interface utilisee par InputPatches.cs ---

        // Note IL2CPP : on evite les parametres "out" avec un struct custom
        // (ex: InputFrame) car ClassInjector.RegisterTypeInIl2Cpp echoue parfois
        // a convertir ce genre de signature (NullReferenceException interne dans
        // Il2CppInterop.Runtime.Injection.ClassInjector.ConvertMethodInfo).
        // On utilise donc deux methodes simples a la place.
        public bool HasCurrentFrame()
        {
            return IsPlayingBack && playbackIndex < currentMovie.Frames.Count;
        }

        public InputFrame GetCurrentFrame()
        {
            return currentMovie.Frames[playbackIndex];
        }

        public void RecordAxisSample(string axisName, float value)
        {
            if (!IsRecording) return;
            if (axisName == "Horizontal") pendingFrame.Horizontal = value;
            else if (axisName == "Vertical") pendingFrame.Vertical = value;
        }

        public void RecordButtonSample(string buttonName, bool value)
        {
            if (!IsRecording) return;
            switch (buttonName)
            {
                case "Jump": pendingFrame.Jump = value; break;
                case "Interact": pendingFrame.Interact = value; break;
                case "Brake": pendingFrame.Brake = value; break;
            }
        }

        // Appele apres chaque pas de simulation manuel : on cloture la frame
        // d'enregistrement courante, ou on avance le curseur de lecture.
        private void AdvanceRecordingOrPlayback()
        {
            saveStates.CaptureRewindFrame();
            if (IsRecording)
            {
                currentMovie.Frames.Add(pendingFrame);
                pendingFrame = default;
            }

            if (IsPlayingBack)
            {
                playbackIndex++;
                if (playbackIndex >= currentMovie.Frames.Count)
                {
                    IsPlayingBack = false;
                    Debug.Log("[PostbirdTAS] Fin de la movie.");
                }
            }
        }
    }
}

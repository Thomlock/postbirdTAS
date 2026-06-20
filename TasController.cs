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

        private Rect windowRect = new Rect(20, 20, 220, 320);
        private bool showUI = true;

        private void OnGUI()
        {
            if (!showUI) return;
            windowRect = GUI.Window(0, windowRect, (GUI.WindowFunction)DrawWindow, "PostbirdTAS");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label($"Frame avance : {IsFrameAdvanceMode}");
            GUILayout.Label($"Enregistrement : {IsRecording}");
            GUILayout.Label($"Lecture : {IsPlayingBack} (frame {playbackIndex})");
            GUILayout.Label($"Edition : {IsEditMode}");

            GUILayout.Space(8);

            if (GUILayout.Button(IsFrameAdvanceMode ? "Reprendre (F1)" : "Pause (F1)"))
                IsFrameAdvanceMode = !IsFrameAdvanceMode;

            if (GUILayout.Button("Avancer 1 frame (F2)") && IsFrameAdvanceMode)
                stepRequested = true;

            GUILayout.Space(4);

            if (GUILayout.Button("Save state (F5)"))
                saveStates.SaveState(0);

            if (GUILayout.Button("Load state (F7)"))
                saveStates.LoadState(0);

            GUILayout.Space(4);

            if (GUILayout.Button(IsRecording ? "Stop enregistrement (F9)" : "Enregistrer (F9)"))
                ToggleRecording();

            if (GUILayout.Button(IsPlayingBack ? "Stop lecture (F10)" : "Lire movie (F10)"))
                TogglePlayback();

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Frame cible:", GUILayout.Width(70));
            // Affichage en lecture seule uniquement : la saisie se fait via les
            // touches 0-9/Retour arriere lues directement par NativeKeys (cf.
            // HandleHotkeys), pas via ce widget. Un vrai GUILayout.TextField/
            // GUI.TextField plante ici sous IL2Cpp : UnityEngine.GUI.DoTextField
            // appelle UnityEngine.GUIStateObjects.GetStateObject, une methode
            // dont Il2CppInterop echoue a reconstruire le "unstripping"
            // ("System.NotSupportedException: Method unstripping failed"). On
            // evite donc tout appel a GUI.TextField/GUILayout.TextField.
            GUILayout.Label(seekInputActive
                ? (string.IsNullOrEmpty(seekInputBuffer) ? "_" : seekInputBuffer)
                : "(F3)", GUILayout.Width(60));
            if (GUILayout.Button("Aller") && int.TryParse(seekInputBuffer, out int target))
                SeekToFrame(target);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            if (GUILayout.Button(IsEditMode ? "Quitter edition (F4)" : "Editer (F4)"))
                IsEditMode = !IsEditMode;

            GUI.DragWindow();
        }


        private void Awake()
        {
            Instance = this;
        }

        private bool stepping;
        private bool manualPhysicsActive;

        // En mode frame-advance (et pendant un seek), on prend la main sur la
        // simulation physique au lieu de laisser Unity l'appeler automatiquement.
        // C'est ce qui rend l'avance frame par frame deterministe : avec la
        // simulation automatique, le nombre de FixedUpdate declenches au cours
        // d'une seule frame de jeu depend du temps reel ecoule (accumulateur de
        // pas fixe d'Unity), donc selon le framerate du moment un meme appui sur
        // F2 pouvait produire 0, 1 ou 2 pas de physique. C'etait la cause du bug
        // d'acceleration du velo non geree de facon fiable en frame par frame
        // (le Rigidbody du velo recevait zero, une, ou deux fois sa force
        // d'acceleration pour une seule frame "logique"). Avec
        // Physics.autoSimulation = false, Unity n'appelle plus jamais
        // FixedUpdate tout seul : seul un appel explicite a Physics.Simulate(...)
        // declenche un pas, et on en fait exactement un par frame avancee.
        //
        // Note : Physics.simulationMode/SimulationMode (Unity 2020.2+) n'existe
        // pas dans la version d'Unity utilisee par ce jeu (erreur de build
        // CS0117/CS0103) ; on utilise donc l'API plus ancienne Physics.autoSimulation,
        // qui a le meme effet. Pour la meme raison, Physics.Simulate retourne
        // void ici (et non bool comme dans les versions recentes) : pas de test
        // sur sa valeur de retour.
        private void EnableManualPhysics()
        {
            if (manualPhysicsActive) return;
            Physics.autoSimulation = false;
            manualPhysicsActive = true;
        }

        private void DisableManualPhysics()
        {
            if (!manualPhysicsActive) return;
            Physics.autoSimulation = true;
            manualPhysicsActive = false;
        }

        private void OnDestroy()
        {
            // Au cas ou le mod serait decharge pendant qu'on est en pause/seek :
            // on rend la main au moteur pour ne pas laisser le jeu fige ou avec
            // une simulation physique manuelle orpheline.
            DisableManualPhysics();
            Time.timeScale = 1f;
        }

        private void Update()
        {
            HandleHotkeys();

            if (isFastForwarding)
            {
                // FastForwardCoroutine pilote elle-meme timeScale et le mode de
                // simulation physique pendant un seek (F3) : on ne touche a
                // rien ici pour ne pas lui rentrer dedans.
                return;
            }

            if (NativeKeys.IsDown(NativeKeys.VK_F4))
            {
                if (saveStates.RewindOneStep())
                    Time.timeScale = 0f; // fige pendant le rewind pour eviter que la physique "rejoue" par-dessus
            }

            if (IsFrameAdvanceMode)
            {
                EnableManualPhysics();

                // Tant qu'on n'est pas en train d'executer un pas, le jeu reste
                // completement fige : Update est fige via timeScale=0, et
                // FixedUpdate ne se declenche plus du tout tout seul grace au
                // mode Script ci-dessus.
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
                DisableManualPhysics();
                Time.timeScale = 1f;
            }
        }

        // Laisse passer exactement une frame moteur a vitesse normale (Update),
        // et exactement UN pas de physique deterministe (FixedUpdate), puis
        // re-fige le jeu. C'est l'equivalent d'un "frame advance" generique,
        // mais fiable quel que soit le framerate reel de la machine.
        private System.Collections.IEnumerator StepOneFrameCoroutine()
        {
            stepping = true;
            Time.timeScale = 1f;

            // Pas physique manuel et deterministe : toujours exactement un seul
            // pas de duree Time.fixedDeltaTime, peu importe le framerate reel.
            // C'est ce qui corrige l'acceleration du velo qui ne s'appliquait
            // pas de facon fiable en mode frame par frame. (Physics.Simulate
            // retourne void dans la version d'Unity de ce jeu, pas bool : pas
            // de test sur sa valeur de retour ici.)
            Physics.Simulate(Time.fixedDeltaTime);

            yield return null; // laisse un Update (rendu + logique non-physique) s'executer

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
            IsFrameAdvanceMode = false; // gere par cette coroutine pendant le seek, restaure a la fin
            EnableManualPhysics();      // un pas physique exact par frame rejouee, cf. StepOneFrameCoroutine
            Time.timeScale = 1f;        // sert uniquement a faire avancer Update(), pas la physique

            // On enchaine plusieurs pas physiques avant de relacher la main au
            // moteur pour le rendu. Avant, Time.timeScale = 20f laissait Unity
            // declencher 0, 1 ou plusieurs FixedUpdate par frame de rendu de
            // facon imprevisible (selon le framerate reel), ce qui appliquait
            // l'acceleration du velo un nombre de fois different de
            // l'enregistrement original et faisait diverger la trajectoire
            // rejouee de la trajectoire reelle. Ici, chaque frame de la movie
            // correspond toujours a exactement un appel Physics.Simulate, comme
            // pendant l'enregistrement initial : le seek reproduit fidelement
            // la meme physique, juste plus vite.
            const int stepsPerRenderedFrame = 50;
            int stepsThisChunk = 0;

            while (playbackIndex < targetFrame && IsPlayingBack)
            {
                Physics.Simulate(Time.fixedDeltaTime);
                AdvanceRecordingOrPlayback();

                if (++stepsThisChunk >= stepsPerRenderedFrame)
                {
                    stepsThisChunk = 0;
                    yield return null; // garde l'UI/le rendu reactifs pendant un long seek
                }
            }

            if (stepsThisChunk > 0)
                yield return null;

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
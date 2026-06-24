# PostbirdTAS

Mod BepInEx (IL2CPP) pour faire du tool-assisted speedrun sur _Postbird in
Provence_. Fournit : pause/avance frame par frame, save states, rewind
automatique, saut a une frame precise, edition d'un enregistrement, une
interface graphique avec boutons, et enregistrement/lecture d'une sequence
d'inputs ("movie").

## Ce qui est verifie vs. ce qui ne l'est pas

J'ai inspecte `global-metadata.dat` extrait de l'installeur (sans executer le
jeu) et confirme la presence de ces classes/membres dans le binaire :
`MainPlayerController`, `Bike`, `BikeSettings`, `DeliveryManager`, `Package`,
`SaveManager`, `Timer`, `InputManager`, `Speed`, `MaxSpeed`, `Acceleration`,
`IsPaused`, `Pause`, `Resume`, `CurrentPackage`, `OnPackageDelivered`.

Je n'ai en revanche **pas** pu generer un dump IL2CPP complet (namespaces,
signatures exactes, visibilite) car l'acces a NuGet est bloque dans mon
environnement. Le mod fonctionne donc en deux couches :

- **Couche generique (fiable a 100%)** : avance frame par frame via
  `Physics.Simulate`, save state de la position/rotation/vitesse du joueur
  (Transform + Rigidbody), enregistrement/lecture des entrees via les patches
  Harmony sur `UnityEngine.Input`. Rien de tout ca ne depend des classes
  internes du jeu.
- **Couche specifique au jeu (best-effort)** : tentative de sauvegarder
  aussi `Speed`/`MaxSpeed`/`IsPaused`/`CurrentPackage` sur le composant
  `MainPlayerController`, par reflexion (donc sans planter si les noms ou
  types reels different un peu).

## Etape 0 — generer les vrais stubs d'interop (a faire toi-meme)

1. Installe le jeu normalement, puis telecharge **BepInEx 6 IL2CPP (build
   bleeding edge)** : https://builds.bepinex.dev/projects/bepinex_be
   Peut importe la version de BepInEx il faut juste quelle soit en IL2CPP
3. Decompresse son contenu directement dans le dossier du jeu (a cote de
   `PostbirdInProvence.exe` (de base il est dans Program Files (x86))).
   
5. Lance le jeu une fois, attends l'arrivee au menu principal, puis quitte.
   Si le jeu ne se lance pas lance le en administrateur depuis le fichier exe
   du jeu
   
   Ca genere `BepInEx/interop/*.dll`, les assemblies managees reconstituees
   a partir de `GameAssembly.dll`.
7. Décompresse le fichier du mod(postbirdTAS) à la racine du jeu. 
   
## Compilation

1. Ouvre `PostbirdTAS.csproj`, modifie `GameInteropPath` et `GameManagedPath`
   pour pointer vers ton dossier `BepInEx` genere a l'etape 0.
   
3. `dotnet build -c Release` (ou ouvre le dossier dans Visual Studio /
   Rider).
   
5. La DLL compilee est copiee automatiquement dans
   `<jeu>/BepInEx/plugins/PostbirdTAS/PostbirdTAS.dll`.
4

## Touches

| Touche         | Action                                                                                        |
| -------------- | --------------------------------------------------------------------------------------------- |
| F1             | Active/desactive le mode pause + avance frame                                                 |
| F2             | Avance d'une frame. Maintenue, avance automatiquement a 5 frames/seconde apres un court delai |
| F3             | Active/desactive la saisie d'un numero de frame cible (pendant la lecture d'une movie)        |
| F4             | Active/desactive le mode edition d'une movie chargee (exige F10 + F1 actifs)                  |
| F5             | Sauvegarde l'etat courant (slot 0)                                                            |
| F7             | Recharge l'etat sauvegarde (slot 0)                                                           |
| F9             | Demarre / arrete l'enregistrement d'une movie                                                 |
| F10            | Lit la derniere movie enregistree (`PostbirdTAS_movie.tas`, a la racine du jeu)               |
| F12            | Affiche/masque l'overlay graphique (utile pour filmer sans l'interface a l'ecran)             |
| 0-9            | (saisie F3 active) compose le numero de frame cible                                           |
| Retour arriere | (saisie F3 active) efface le dernier chiffre tape                                             |
| Entree         | (saisie F3 active) valide le numero et lance le saut (rejoue les inputs en accelere)          |
| Fleches        | (mode edition F4 actif) ajustent Horizontal/Vertical de la frame courante par pas de 0.1      |
| J / I / B      | (mode edition F4 actif) togglent Jump / Interact / Brake de la frame courante                 |
| S              | (mode edition F4 actif) sauvegarde la movie modifiee sur disque                               |

Workflow typique : F1 pour passer en pause/avance frame, F9 pour enregistrer,
F2 pour avancer d'une frame (ou la maintenir pour un defilement rapide a 5
fps) en testant des inputs, F5/F7 pour revenir en arriere et reessayer un
passage jusqu'a trouver l'optimal, F9 pour arreter l'enregistrement, F10
pour relire la movie complete.

Pendant une lecture (F10), F3 permet de sauter directement a une frame
donnee : tape le numero puis valide avec Entree. Le saut recharge l'etat du
debut de la movie puis rejoue les inputs en accelere jusqu'a la frame visee
(c'est la seule methode fiable, le jeu n'etant pas "seekable" sans rejouer
sa simulation), avant de se refiger automatiquement en mode pause.

Pour corriger une movie existante sans tout recommencer : charge-la avec
F10, navigue jusqu'a la frame a corriger (F2/F3), active F4, ajuste les
inputs avec les fleches et J/I/B, puis sauvegarde avec S. Recharge ensuite
la movie (F10 a nouveau) pour verifier le resultat.

## Interface graphique

En plus du clavier, une petite fenetre a l'ecran (deplacable a la souris)
reprend les actions principales sous forme de boutons : pause/avance frame,
avancer d'une frame, save/load state, demarrer/arreter l'enregistrement,
lire une movie, saisir une frame cible et lancer le saut, et activer le
mode edition. Elle affiche aussi l'etat courant du mod (frame actuelle,
enregistrement en cours, etc.). F12 permet de la masquer, par exemple pour
capturer une video sans overlay a l'image.

## Rewind automatique

En plus des save states manuels (F5/F7), le mod capture automatiquement un
historique glissant de l'etat du joueur a chaque frame avancee (limite a
600 frames, soit environ 10 secondes a 60 fps, pour eviter une consommation
memoire excessive). Cet historique sert de base au systeme de seek (F3) et
peut etre etendu a un rewind pas-a-pas independant des save states fixes.

## Limites connues

- Un seul slot de save state manuel pour l'instant (facile a etendre :
  passe un numero de slot different a `SaveState`/`LoadState`). Le slot 99
  est reserve en interne comme ancre pour le systeme de seek (F3).
- Les noms d'axes par defaut ("Horizontal", "Vertical", "Jump", "Interact",
  "Brake") sont des suppositions raisonnables a verifier/adapter.
- L'interface graphique utilise `OnGUI`/`GUI.Window`, dont le delegue
  `GUI.WindowFunction` est un type Il2Cpp : passer directement une methode
  C# (`DrawWindow`) ou utiliser `new GUI.WindowFunction(DrawWindow)` peut
  echouer a la compilation selon la version d'Il2CppInterop. Le cast
  explicite `(GUI.WindowFunction)DrawWindow` est la forme qui fonctionne de
  facon fiable. Pour la meme raison, evite tout `GUI.TextField`/
  `GUILayout.TextField` : sous IL2Cpp, `GUI.DoTextField` appelle
  `UnityEngine.GUIStateObjects.GetStateObject`, une methode que
  Il2CppInterop ne reussit pas a "unstripper" (l'erreur observee est
  `System.NotSupportedException: Method unstripping failed`). Le champ
  "Frame cible" de la fenetre est donc un simple `GUILayout.Label` en
  lecture seule ; la saisie reelle passe par `NativeKeys` (touches 0-9,
  Retour arriere, Entree lues directement au niveau OS).
- La physique est pilotee manuellement (`Physics.autoSimulation = false` +
  `Physics.Simulate(Time.fixedDeltaTime)`) pendant le mode pause/avance
  frame (F1/F2) et pendant un seek (F3), au lieu de laisser Unity
  declencher `FixedUpdate` automatiquement. C'est ce qui rend
  l'acceleration du velo deterministe en frame par frame : avec le mode
  automatique, le nombre de `FixedUpdate` declenches pendant une seule
  frame avancee depend du temps reel ecoule, donc selon le framerate un
  meme F2 pouvait appliquer 0, 1 ou 2 fois la force d'acceleration. Idem
  pour le seek : l'ancien `Time.timeScale = 20f` pouvait declencher
  plusieurs `FixedUpdate` par frame de la movie et faire diverger la
  trajectoire rejouee de l'originale ; chaque frame rejouee correspond
  desormais a exactement un `Physics.Simulate`, comme pendant
  l'enregistrement. Le jeu utilisant une version d'Unity anterieure a
  2020.2, on passe par l'API historique `Physics.autoSimulation` (et non
  `Physics.simulationMode`/`SimulationMode`, introduits en 2020.2 et
  absents des stubs d'interop generes pour ce jeu) ; dans cette version,
  `Physics.Simulate` retourne `void` (pas `bool`).
- Aucune categorie TAS n'existe sur la page speedrun.com du jeu : ce sera
  une premiere si tu publies une run.

## Avertissement

Ce mod modifie uniquement ta propre copie locale du jeu (gratuit, hors
ligne, solo) a des fins de speedrun assiste par outils — pratique courante
et acceptee dans la communaute speedrunning (voir TASVideos.org). Il n'a
aucun usage en ligne/multijoueur et ne contourne aucune protection
anti-triche.

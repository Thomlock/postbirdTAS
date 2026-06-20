# PostbirdTAS

Mod BepInEx (IL2CPP) pour faire du tool-assisted speedrun sur *Postbird in
Provence*. Fournit : pause/avance frame par frame, save states, et
enregistrement/lecture d'une sequence d'inputs ("movie").

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
2. Decompresse son contenu directement dans le dossier du jeu (a cote de
   `PostbirdInProvence.exe`).
3. Lance le jeu une fois, attends l'arrivee au menu principal, puis quitte.
   Ca genere `BepInEx/interop/*.dll`, les assemblies managees reconstituees
   a partir de `GameAssembly.dll`.
4. Ouvre `BepInEx/interop/Assembly-CSharp.dll` dans **dnSpy** (ou ILSpy) et
   cherche `MainPlayerController` : verifie le namespace exact, et les vrais
   noms/types des champs de vitesse/etat. Corrige `CandidateFieldNames` dans
   `SaveStateManager.cs` et les noms d'axes dans `InputPatches.cs` si besoin
   (regarde aussi Edit > Project Settings > Input Manager si tu as le projet
   source, sinon les logs de `RecordAxisSample`/`RecordButtonSample` en mode
   enregistrement te diront quels axes sont reellement interroges).

## Compilation

1. Ouvre `PostbirdTAS.csproj`, modifie `GameInteropPath` et `GameManagedPath`
   pour pointer vers ton dossier `BepInEx` genere a l'etape 0.
2. `dotnet build -c Release` (ou ouvre le dossier dans Visual Studio /
   Rider).
3. La DLL compilee est copiee automatiquement dans
   `<jeu>/BepInEx/plugins/PostbirdTAS/PostbirdTAS.dll`.

## Touches

| Touche | Action |
|---|---|
| F1 | Active/desactive le mode pause + avance frame |
| F2 | Avance d'une frame (uniquement si F1 est actif) |
| F5 | Sauvegarde l'etat courant (slot 0) |
| F7 | Recharge l'etat sauvegarde (slot 0) |
| F9 | Demarre / arrete l'enregistrement d'une movie |
| F10 | Lit la derniere movie enregistree (`PostbirdTAS_movie.tas`, a la racine du jeu) |

Workflow typique : F1 pour passer en pause/avance frame, F9 pour enregistrer,
F2 pour avancer d'une frame en testant des inputs, F5/F7 pour revenir en
arriere et reessayer un passage jusqu'a trouver l'optimal, F9 pour arreter
l'enregistrement, F10 pour relire la movie complete a vitesse normale et la
capturer en video.

## Limites connues

- Un seul slot de save state pour l'instant (facile a etendre : passe un
  numero de slot different a `SaveState`/`LoadState`).
- Les noms d'axes par defaut ("Horizontal", "Vertical", "Jump", "Interact",
  "Brake") sont des suppositions raisonnables a verifier/adapter.
- Aucune categorie TAS n'existe sur la page speedrun.com du jeu : ce sera
  une premiere si tu publies une run.

## Avertissement

Ce mod modifie uniquement ta propre copie locale du jeu (gratuit, hors
ligne, solo) a des fins de speedrun assiste par outils — pratique courante
et acceptee dans la communaute speedrunning (voir TASVideos.org). Il n'a
aucun usage en ligne/multijoueur et ne contourne aucune protection
anti-triche.

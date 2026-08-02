# Plugin Logi « EverQuest Spells »

Plugin C# pour Logi Plugin Service / MX Creative Console. Il expose :

- **9 actions « Sort 1 … Sort 9 »** (groupe *Sorts EverQuest*) : chaque touche affiche
  l'icône extraite du jeu (`icons\spell_N@128.png`) et envoie **ALT + la touche
  physique du chiffre correspondant** (sur AZERTY : ALT+&, ALT+é, ALT+", ALT+',
  ALT+(, ALT+-, ALT+è, ALT+_, ALT+ç).
- **1 action « Mettre à jour les icônes »** : lance `update-spell-icons.ps1` (run
  complet) en arrière-plan et rafraîchit les 9 touches quand les PNG changent.
- **1 action « Mise à jour auto (30 s) »** : bascule le rafraîchissement de fond,
  **actif par défaut**. Le minuteur ne lance rien si EverQuest n'est pas lancé, et
  n'exécute jamais deux runs en parallèle. Il appelle le script en mode `-Quick` :
  ~2 s, grille en cache uniquement, abandon silencieux si la lecture est douteuse.

Statut : chargé, affiché et validé en jeu (affichage et frappe ALT+chiffre).

## Affectation sur le keypad

1. Ouvrir Logi Options+ → MX Creative Console.
2. Dans le panneau d'actions à droite, cliquer sur **« TOUTES LES ACTIONS »** en haut :
   par défaut il est filtré sur « Actions Système » et le plugin n'y apparaît pas.
   La loupe fonctionne aussi — taper `Sort`.
3. Les actions sont dans le groupe **Sorts EverQuest**.
4. Glisser « Sort 1 » … « Sort 9 » sur les 9 touches, et « Mettre à jour les icônes »
   sur une touche libre (ou une autre page).

Le plugin est universel (pas d'application associée) : ses actions sont disponibles
dans n'importe quel profil d'application, il n'y a pas de profil « EverQuest » à créer.
Si le groupe n'apparaît pas après un rechargement, fermer complètement Options+ et le
relancer — le service, lui, garde le plugin chargé.

## Compilation

Prérequis : SDK .NET 10 (`winget install Microsoft.DotNet.SDK.10`).

```bash
dotnet build "C:\Users\user\Documents\Everquest Logi\app\plugin\EverQuestPlugin\EverQuestPlugin.csproj" -c Release
```

Le build écrit un fichier `EverQuestPlugin.link` dans le dossier des plugins du
service et déclenche `loupedeck:plugin/EverQuest/reload` : le plugin est rechargé
à chaud, sans redémarrer Options+.

Journal : `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\EverQuest.log`

## Détails d'implémentation (pièges rencontrés)

- **`net10.0` obligatoire.** `PluginApi.dll` du service est compilé pour .NET 10 ;
  cibler `net8.0` (comme le DemoPlugin officiel) échoue à la compilation (CS1705).
- **Une classe `ClientApplication` est obligatoire**, même pour un plugin
  « API-only » sans application associée. Le chargeur exige *exactement une* classe
  `Plugin` **et** *exactement une* classe `ClientApplication` dans l'assembly ;
  sans elle, le service refuse le plugin avec un laconique « Cannot load plugin ».
  D'où [EverQuestApplication.cs](EverQuestPlugin/EverQuestApplication.cs).
- **Ne pas copier `PluginApi.dll`** à côté du plugin (`<Private>false</Private>`) :
  le service fournit déjà l'assembly.
- **Frappes : utiliser l'API du SDK, pas `SendInput`.**
  `ClientApplication.SendKeyboardShortcut(VirtualKeyCode.Key1..Key9, ModifierKey.Alt)`
  emprunte le même chemin que l'action « raccourci clavier » intégrée. `Key1..Key9`
  sont les touches **physiques** de la rangée des chiffres : sur AZERTY, ALT+Key1
  produit bien ALT+&. Sûr ici car `SendKeyboardShortcut` n'active l'application
  associée que si `HasNoApplication` est faux — ce n'est pas notre cas, donc pas de
  vol de focus.
  *Première version ratée* : un P/Invoke `SendInput` maison échouait en silence —
  la structure `INPUT` déclarée avec la seule union clavier fait 32 octets alors que
  Windows en attend 40 (l'union doit être dimensionnée pour `MOUSEINPUT`), donc
  `cbSize` était invalide et l'appel rejeté sans erreur visible.
- **Image plein écran sur la touche.** Deux réglages sont nécessaires :
  `DefaultIconTemplate.ict` doit décrire une zone image en 0,0,100,100 avec l'élément
  texte en `isVisible: false` (le gabarit du DemoPlugin réserve 30 % de hauteur au
  texte, d'où un libellé sous l'image), et `GetCommandImage` doit dessiner avec
  `DrawImage(image, 0, 0, builder.Width, builder.Height)` pour étirer bord à bord.
- **`Icon256x256.png` et `DefaultIconTemplate.ict`** sont attendus dans
  `package/metadata` : tous les plugins officiels installés en ont.
- **Mode développeur** : activé au passage via `loupedeck:developer/mode/enable`
  (réglage `Loupedeck/DeveloperMode` dans `LoupedeckSettings.ini`). Il s'est avéré
  *non nécessaire* au chargement — pour le désactiver : `loupedeck:developer/mode/disable`.
- **Aucun chemin en dur.** `EverQuestPlugin.AppDir` remonte depuis l'emplacement du DLL
  jusqu'au dossier contenant `update-spell-icons.ps1`, donc le dossier `app` peut être
  déplacé ou copié sur une autre machine sans rien éditer.
- **Diagnostiquer un échec silencieux du service.** Les binaires Logi sont obfusqués
  (chaînes remplacées par `by.(id)`). On peut les lire en chargeant
  `LoupedeckService.dll` par réflexion et en appelant le déchiffreur de chaînes ; c'est
  comme ça qu'ont été trouvés la commande exacte du mode développeur et la vraie raison
  du refus de chargement.

## Fichiers

| Fichier | Rôle |
|---|---|
| `EverQuestPlugin.cs` | Classe `Plugin`, résolution du dossier de l'app, démarrage du minuteur |
| `IconUpdater.cs` | Lancement du script (manuel/périodique), verrou anti-recouvrement |
| `AutoUpdateCommand.cs` | Bascule du rafraîchissement automatique |
| `EverQuestApplication.cs` | Classe `ClientApplication` requise par le chargeur |
| `SpellCommand.cs` | Les 9 touches de sorts (image + frappe), watcher de fichiers |
| `UpdateIconsCommand.cs` | Bouton « Mise à jour » |
| `KeyboardHelper.cs` | Envoi de ALT + rangée des chiffres via l'API clavier du SDK |
| `package/metadata/LoupedeckPackage.yaml` | Manifeste du plugin |

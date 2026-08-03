# Plugin Logi « EverQuest Spells »

Plugin C# pour Logi Plugin Service / MX Creative Console. **Autonome** : un seul DLL,
qui fait lui-même la capture de la fenêtre du jeu, la reconnaissance des icônes,
l'affichage et l'envoi des frappes.

## Actions exposées

- **9 actions « Sort 1 … Sort 9 »** (groupe *Sorts EverQuest*) : chaque touche affiche
  l'icône lue dans le jeu et envoie **ALT + la touche physique du chiffre**
  (sur AZERTY : ALT+&, ALT+é, ALT+", ALT+', ALT+(, ALT+-, ALT+è, ALT+_, ALT+ç).
- **Mettre à jour les icônes** : force une relecture complète, y compris la
  relocalisation de la barre. À utiliser après l'avoir déplacée ou changé de résolution.
- **Mise à jour auto** : bascule le rafraîchissement de fond, **actif par défaut**,
  cadencé à 5 s (`IconUpdater.DefaultIntervalSeconds`).

## Affectation sur le keypad

1. Ouvrir Logi Options+ → MX Creative Console.
2. Dans le panneau de droite, cliquer sur **« TOUTES LES ACTIONS »** en haut : par
   défaut il est filtré sur « Actions Système » et le plugin n'y apparaît pas.
   La loupe fonctionne aussi — taper `Sort`.
3. Glisser « Sort 1 » … « Sort 9 » sur les 9 touches.

Le plugin est universel (pas d'application associée) : ses actions sont disponibles
dans n'importe quel profil. Si le groupe n'apparaît pas après un rechargement, fermer
complètement Options+ et le relancer.

## Compilation

```bash
dotnet build plugin/EverQuestPlugin/EverQuestPlugin.csproj -c Release
```

Le build écrit un fichier `EverQuestPlugin.link` dans le dossier des plugins du service
et déclenche `loupedeck:plugin/EverQuest/reload` : rechargement à chaud sans redémarrer
Options+. Journal : `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\EverQuest.log`

> **Attention** : ce `.link` contient un chemin absolu. Compiler une *copie* du dépôt
> détourne le service vers cette copie. Pour revenir en arrière, recompiler depuis le
> bon dossier ou corriger le fichier.

## Fichiers

| Fichier | Rôle |
|---|---|
| `EverQuestPlugin.cs` | Classe `Plugin` : dossier de données, démarrage du lecteur et du minuteur |
| `SpellBarReader.cs` | Le cœur : capture, localisation, reconnaissance, état, images des touches |
| `EqGame.cs` | Découverte de l'installation, réglages du personnage, packs d'icônes, capture |
| `EqIconLib.cs` | Décodeur TGA, corrélation croisée normalisée, détection de périodicité |
| `IconUpdater.cs` | Ordonnancement des lectures (minuteur, anti-recouvrement) |
| `SpellCommand.cs` | Les 9 touches de sorts |
| `UpdateIconsCommand.cs` / `AutoUpdateCommand.cs` | Les deux touches de service |
| `KeyboardHelper.cs` | Envoi de ALT + rangée des chiffres via l'API clavier du SDK |
| `EverQuestApplication.cs` | Classe `ClientApplication` requise par le chargeur |

## Pièges du SDK Logi (durement acquis)

- **`net10.0` obligatoire.** `PluginApi.dll` du service est en .NET 10 ; cibler
  `net8.0` (comme le DemoPlugin officiel) ne compile pas (CS1705).
- **Une classe `ClientApplication` est obligatoire**, même sans application associée.
  Le chargeur exige *exactement une* classe `Plugin` **et** *exactement une*
  `ClientApplication` ; sinon le service refuse le plugin avec un laconique
  « Cannot load plugin ».
- **Ne pas copier les assemblies du service** à côté du plugin (`<Private>false</Private>`).
  `PluginApi`, `System.Drawing.Common` et ses deux dépendances internes
  (`System.Private.Windows.Core`, `System.Private.Windows.GdiPlus`) sont fournies par le
  service : on les référence depuis son dossier, jamais via NuGet.
- **Frappes : utiliser l'API du SDK**, `ClientApplication.SendKeyboardShortcut(
  VirtualKeyCode.Key1..Key9, ModifierKey.Alt)`. `Key1..Key9` sont les touches
  *physiques* de la rangée des chiffres. Sans danger ici : l'API n'active l'application
  associée que si `HasNoApplication` est faux.
  *Piège évité* : un `SendInput` maison échouait en silence — la structure `INPUT`
  déclarée avec la seule union clavier fait 32 octets alors que Windows en attend 40.
- **Image plein cadre sur la touche** : `DefaultIconTemplate.ict` avec zone image
  0,0,100,100 et l'item texte en `isVisible: false`, plus `GetCommandImage` qui dessine
  avec `DrawImage(img, 0, 0, builder.Width, builder.Height)`.
- **Ne rien mettre de statique qui appartienne à une instance de plugin.** Le service
  charge la nouvelle instance *avant* de décharger l'ancienne : avec un minuteur
  statique, l'instance sortante supprimait celui de l'entrante, laissant le plugin
  silencieusement inerte pendant que l'ancienne continuait à travailler. D'où un
  `IconUpdater` par instance.
- **Écrire les nombres en culture invariante.** Une interpolation `$"{x}"` produit
  « 22,25 » sur une machine française, que le parseur invariant refuse ensuite : la
  calibration était perdue à chaque redémarrage et le plugin relançait une localisation
  complète de 55 s à chaque cycle.
- **Diagnostiquer un échec silencieux du service** : ses binaires sont obfusqués
  (chaînes remplacées par `by.(id)`). On peut les lire en chargeant
  `LoupedeckService.dll` par réflexion et en appelant le déchiffreur de chaînes.

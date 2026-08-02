# EverQuest → Logi MX Creative Keypad : extracteur d'icônes de sorts

Extrait automatiquement les icônes des 9 premières gemmes de sorts de la fenêtre
EverQuest ("EverQuest Legends") et les sauvegarde en PNG pleine qualité, prêtes à
être affichées sur les 9 touches du MX Creative Keypad.

## Utilisation

```powershell
powershell -ExecutionPolicy Bypass -File .\update-spell-icons.ps1
```

En pratique on ne lance jamais ce script à la main : le plugin l'appelle tout seul
toutes les 30 s en mode `-Quick`, et la touche « Mettre à jour les icônes » déclenche
un run complet. Le run complet reste le seul à pouvoir relocaliser la barre si tu l'as
déplacée.

### Mode `-Quick` (utilisé par le rafraîchissement automatique)

Un cycle de veille doit être discret et ne jamais dégrader ce qui est affiché :

- il n'utilise que la grille en cache — pas de relocalisation (~2 s au lieu de ~25 s) ;
- si le jeu est fermé, minimisé, ou si la lecture est douteuse, il sort avec le
  code 2 sans rien toucher ;
- il n'écrit un PNG que si les octets diffèrent réellement, pour ne pas réveiller
  le plugin inutilement.

Codes de sortie : `0` = exécuté, `1` = erreur, `2` = rien à faire (le plugin
l'interprète comme « cycle silencieux » et ne signale rien).

Prérequis : le jeu tourne, fenêtre non minimisée (pas besoin qu'elle ait le focus).
Aucune interaction avec le jeu : capture passive (`PrintWindow`) + lecture des
fichiers du jeu uniquement.

## Sorties (dossier `icons\`)

| Fichier | Contenu |
|---|---|
| `spell_1.png` … `spell_9.png` | Icône native 40×40 extraite des planches du jeu |
| `spell_1@128.png` … | Version 128×128 (agrandissement net, pixel-art) |
| `contact.png` | Planche de contrôle : gemme capturée vs icône sauvée (runs complets seulement) |
| `manifest.json` | Par gemme : planche source, index, score de confiance |
| `state.json` | Mémoire du choix par gemme, base de la règle « collante » ci-dessous |

Le premier réflexe de diagnostic est d'ouvrir `contact.png` : la colonne de gauche est
ce qui a été capturé à l'écran, celle de droite l'icône retenue. Si les deux colonnes
correspondent ligne par ligne, la chaîne est correcte.

## Comment ça marche

1. **Découverte du jeu** : dossier d'installation trouvé via le processus `eqgame` en
   cours, sinon le registre (entrées de désinstallation), sinon les emplacements
   habituels sur tous les disques. Aucun chemin en dur.
2. **Lecture des réglages** : le fichier `UI_<perso>_<serveur>.ini` du personnage donne
   le skin actif (`UISkin`) et la position horizontale de la barre (`CastSpellWnd/XPos`).
3. **Capture** de la fenêtre via `PrintWindow` (`PW_RENDERFULLCONTENT`, fonctionne avec DirectX).
4. **Localisation de la barre**, en trois niveaux du moins cher au plus cher :
   - grille en cache (`barfit.json`) revalidée à chaque run — ~2 s ;
   - sinon, balayage large avec les **9 icônes déjà connues** comme gabarits (300× moins
     cher que la bibliothèque entière) ;
   - sinon, **détection de périodicité** : les gemmes forment une suite de cellules
     identiques espacées d'un pas fixe. Comparer chaque ligne à celle située un pas plus
     bas localise la barre sur toute la hauteur de la fenêtre en ~8 ms. Le script remonte
     ensuite tant qu'une gemme valide se trouve au-dessus, pour être sûr de partir de la
     première (sinon toutes les icônes seraient décalées).
5. **Choix du pack d'icônes** : le jeu contient trois jeux distincts
   (`Textures\Alternate 1..3` ; les dossiers `uifiles\<skin>` en sont des copies).
   Le script les note tous sur la capture et retient le meilleur — ici Alternate 1
   (classique, fond parchemin) à 0,98 contre 0,62 et 0,75.
6. **Reconnaissance** : corrélation croisée normalisée (NCC), insensible à
   l'assombrissement de l'UI, contre les ~2 260 icônes du pack retenu.
7. **Extraction** : l'icône **source propre** (40×40) est découpée dans la planche
   gagnante — jamais depuis la capture d'écran (sauf fallback si score < 0,80).

## Robustesse

- Un run n'est accepté que si le score moyen ≥ 0,85 ; sinon jusqu'à 3 tentatives
  espacées de 2 s (un cast ou un cooldown peut griser les gemmes au mauvais moment),
  puis échec propre : **les icônes précédentes sont conservées** et une capture de
  debug est sauvée (`debug-capture.png`).
- **Choix « collant » par gemme** (`state.json`). Une gemme en cours de recast est
  dessinée avec une surcharge de progression : son score chute et le gagnant se met
  à osciller entre des icônes quasi identiques (marge de 0,004 observée). Le script
  re-note donc l'icône *déjà affichée* sur la capture courante et ne change que si
  le prétendant fait mieux de plus de `Hysteresis` (0,05) **et** dépasse
  `ChangeScore` (0,90). Observé : sans cette règle une gemme changeait à presque
  chaque cycle ; avec, 4 cycles d'affilée ne touchent aucun fichier.
- Le cache de calibration n'est écrit qu'après un run réussi ; s'il devient invalide
  (barre déplacée, résolution changée), la relocalisation complète (~25 s) se relance
  toute seule au prochain run complet.

## Portabilité

Rien n'est codé en dur : ni le dossier du jeu, ni le pack d'icônes, ni la position de
la barre, ni l'emplacement de l'application (le plugin déduit ce dernier de son propre
DLL). Le dossier `app` peut être déplacé ou copié tel quel.

**Sur une machine neuve**, il suffit de : installer Logi Options+, installer le SDK
.NET 10 (`winget install Microsoft.DotNet.SDK.10`), copier le dossier `app` où l'on
veut, lancer `dotnet build` sur le projet du plugin, puis réaffecter les touches dans
Options+. Supprimer `barfit.json` n'est pas nécessaire mais ne coûte rien : il est
régénéré en ~25 s.

Dépendances à l'exécution : PowerShell 5.1 et le compilateur C# de .NET Framework
(tous deux fournis avec Windows), plus le `PluginApi.dll` du service Logi. Le SDK
.NET 10 ne sert qu'à la compilation.

## Approches écartées (et pourquoi)

À lire avant de « simplifier » quoi que ce soit : chacune de ces pistes a été essayée
et a échoué pour une raison mesurée.

- **Découper les icônes directement dans la capture d'écran.** L'UI d'EverQuest est
  semi-transparente, donc les icônes sont assombries, et l'état du moment (recast,
  surlignage, infobulle) pollue l'image. D'où le principe : la capture sert seulement
  à *identifier* l'icône, jamais à la produire.
- **Chercher la barre en balayant la bibliothèque d'icônes sur toute la hauteur.**
  Coût mesuré : **267 s** par tentative, et surtout résultat *faux* — le balayage s'est
  verrouillé six gemmes plus bas, ce qui décale toutes les icônes. Remplacé par la
  détection de périodicité (8 ms) suivie d'une remontée jusqu'à la première gemme.
- **Se fier à `CastSpellWnd/YPos` de l'INI pour la position verticale.** `XPos` décode
  correctement (erreur ~5 px, la bordure), mais `YPos` donne 171 px d'écart quelle que
  soit l'interprétation testée. Ne pas y retourner.
- **Charger tous les packs d'icônes ensemble.** Les noms de fichiers sont identiques
  d'un pack à l'autre alors que le contenu diffère : dédupliquer par nom écarte
  silencieusement le bon pack. On note chaque pack sur la capture et on garde le
  meilleur.
- **Écrire les PNG à chaque cycle.** Réveille le watcher du plugin et fait clignoter
  les touches pour rien. On compare les octets avant d'écrire.

## Fichiers

- `update-spell-icons.ps1` — le script principal, appelé par le plugin
- `tools\EqIconLib.cs` — décodeur TGA, matcher NCC, détection de périodicité
  (compilé à la volée via `Add-Type`)
- `barfit.json` — cache : grille de la barre + pack d'icônes retenu (supprimable)
- `icons\state.json` — mémoire du choix par gemme (supprimable)

## Plugin Logi

Le plugin C# est dans [plugin/](plugin) — voir son [README](plugin/README.md) pour la
compilation, l'affectation des touches et les pièges du SDK Logi.

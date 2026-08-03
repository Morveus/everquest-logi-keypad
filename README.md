# EverQuest → Logi MX Creative Keypad

Plugin Logitech qui affiche sur les 9 touches du MX Creative Keypad les icônes des
9 premières gemmes de sorts d'EverQuest, lues **en direct dans la fenêtre du jeu**, et
qui envoie le raccourci de lancement du sort correspondant.

Tout est dans un seul DLL : capture de la fenêtre, localisation de la barre,
reconnaissance des icônes, affichage et frappe clavier. Aucun script, aucun processus
externe, rien à installer à côté.

## Installation

Prérequis : Logi Options+ (avec Logi Plugin Service) et le SDK .NET 10
(`winget install Microsoft.DotNet.SDK.10`) pour compiler.

```bash
dotnet build plugin/EverQuestPlugin/EverQuestPlugin.csproj -c Release
```

Le plugin est alors chargé à chaud. Il reste à affecter les actions aux touches :
voir le [README du plugin](plugin/README.md).

## Ce que fait le plugin

| Action | Rôle |
|---|---|
| **Sort 1 … Sort 9** | Affiche l'icône du sort et envoie ALT + la touche du chiffre |
| **Mettre à jour les icônes** | Force une relecture complète (après avoir déplacé la barre) |
| **Mise à jour auto** | Active/coupe le rafraîchissement de fond (actif par défaut, 5 s) |

Les icônes se mettent à jour toutes les 5 secondes sans rien faire. Changer un sort
mémorisé se voit sur la touche au cycle suivant.

## Comment il reconnaît les icônes

Pas d'apprentissage automatique : le problème est *fermé*, on possède déjà toutes les
réponses possibles — ce sont les fichiers d'icônes du jeu. La question n'est donc pas
« qu'est-ce que cette image ? » mais « laquelle de ces 2 262 images connues est-ce ? ».

1. **Découverte du jeu** : dossier trouvé via le processus `eqgame` en cours, sinon le
   registre, sinon les emplacements habituels. Aucun chemin en dur.
2. **Réglages du personnage** : le fichier `UI_<perso>_<serveur>.ini` donne le skin
   actif et la position horizontale de la barre.
3. **Capture** de la fenêtre par `PrintWindow` (`PW_RENDERFULLCONTENT`) : passive,
   fonctionne avec DirectX, sans focus et sans toucher au jeu.
4. **Choix du pack d'icônes** : le jeu contient trois jeux distincts
   (`Textures\Alternate 1..3` ; les dossiers `uifiles` en sont des copies). Chacun est
   noté sur la capture, le meilleur est retenu.
5. **Reconnaissance** : chaque icône, de référence comme capturée, est rééchantillonnée
   en 24×24 RVB (1 728 valeurs), puis centrée et normalisée. Le score est leur produit
   scalaire, c'est-à-dire la **corrélation croisée normalisée**. Cette normalisation rend
   la comparaison insensible à l'assombrissement de l'interface : on compare la structure
   de l'image, pas ses valeurs absolues. Bon match : 0,96 à 0,99. Mauvais : 0,2 à 0,6.
6. **Localisation de la barre** : par **périodicité**. Les gemmes forment une suite de
   cellules identiques espacées d'un pas fixe ; comparer chaque ligne à celle située un
   pas plus bas repère la barre sur toute la hauteur en ~8 ms. Le plugin remonte ensuite
   tant qu'une gemme valide existe au-dessus, sinon toutes les icônes seraient décalées.

## Coût et réactivité

| | Mesuré |
|---|---|
| Cycle de veille (rien n'a changé) | **0,107 s**, soit 2,1 % d'un cœur à 5 s d'intervalle |
| Localisation complète de la barre | ~55 s (rare : au premier lancement, ou si la barre bouge) |

Le cycle de veille ne repose pas la question complète : il compare chaque gemme au
descripteur de l'icône **déjà affichée**, soit 9 produits scalaires. Ce n'est qu'en cas
d'écart que la gemme concernée est ré-identifiée contre la bibliothèque — quelques
millisecondes de plus, uniquement pour les gemmes concernées.

Deux garde-fous :

- **Une gemme en rechargement ne change pas l'affichage.** Son score chute exactement
  comme le ferait un vrai changement de sort, mais elle ne ressemble alors franchement à
  aucune icône : le seuil de remplacement (0,90) n'est pas atteint, l'icône reste en place.
- **Une gemme illisible est ignorée** (emplacement vide, sort en cours de mémorisation) :
  une zone uniforme ne porte aucune information et ne doit pas être lue comme un changement.

## Données écrites

Le plugin n'écrit que dans son propre dossier
(`%LOCALAPPDATA%\Logi\LogiPluginService\PluginData\EverQuest`) :

| Fichier | Contenu |
|---|---|
| `barstate.txt` | Grille de la barre, pack d'icônes, icône retenue par gemme |
| `icons\spell_1..9.png` | Les icônes affichées, pour inspection |

Supprimer ce dossier ne casse rien : tout est recalculé.

## Approches écartées (et pourquoi)

À lire avant de « simplifier » quoi que ce soit — chacune a été essayée et mesurée.

- **Découper les icônes dans la capture d'écran.** L'interface est semi-transparente et
  l'état du moment (recharge, surlignage, infobulle) pollue l'image. La capture sert à
  *identifier* l'icône, jamais à la produire.
- **Chercher la barre en balayant la bibliothèque sur toute la hauteur.** 267 s par
  tentative, et résultat *faux* : verrouillage six gemmes plus bas, toutes les icônes
  décalées. Remplacé par la périodicité (8 ms) suivie d'une remontée.
- **Se fier à `CastSpellWnd/YPos` pour la position verticale.** `XPos` décode bien
  (~5 px d'erreur), `YPos` donne 171 px d'écart quelle que soit l'interprétation.
- **Dédupliquer les packs d'icônes par nom de fichier.** Les noms sont identiques d'un
  pack à l'autre alors que le contenu diffère : on compare le contenu.
- **Un script PowerShell appelé par le plugin** (l'architecture initiale). Coûtait
  0,42 s de processeur par cycle rien qu'en démarrage de processus et compilation, et
  obligeait à brider les relectures — ce qui retardait les vrais changements jusqu'à
  17 s. Tout porter en C# dans le plugin a réglé les deux à la fois.

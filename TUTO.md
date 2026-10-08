# Jellyfin + Media Uploader avec Docker

## Prérequis

- Docker et Docker Compose v2 (`docker compose version` doit répondre).
- Rien d'autre : le plugin est compilé dans un conteneur, pas besoin d'installer .NET.

## Contenu du dossier

```
Jellyfin.Plugin.MediaUploader/        # racine du dépôt cloné
├── docker-compose.yml                  # Jellyfin + compilation du plugin
├── Jellyfin.Plugin.MediaUploader/      # sources du plugin (+ Dockerfile)
├── jellyfin/                           # config et cache de Jellyfin (créés au lancement)
└── media/
    ├── music/                          # bibliothèque musique
    ├── movies/                         # bibliothèque films
    └── shows/                          # bibliothèque séries et animés
```

## 1. Lancer

```bash
cd Jellyfin.Plugin.MediaUploader   # racine du dépôt cloné
docker compose up -d --build
```

Ce que ça fait : compile le plugin, copie ses DLL (le plugin et TagLibSharp, qui lit les tags) dans `jellyfin/config/plugins/MediaUploader_1.1.1.0/`,
puis démarre Jellyfin. Suivre les logs : `docker compose logs -f jellyfin`.

Si le build du plugin échoue lancez
`docker compose build plugin-builder` et ouvrez une issue avec l'erreur affichée.

## 2. Configurer Jellyfin (première fois)

1. Ouvrez `http://IP_DU_SERVEUR:8096` et passez l'assistant de démarrage.
2. Ajoutez vos bibliothèques :
   - Musique : dossier `/media/music`
   - Films : dossier `/media/movies`
   - Séries et animés (optionnel) : type de contenu **Séries**, dossier `/media/shows`
3. Tableau de bord > Extensions : « Media Uploader » doit apparaître.
   Sinon : `docker compose restart jellyfin`.

## 3. Configurer le plugin (administrateur)

Tableau de bord > Extensions > **Media Uploader** (clic sur la fiche du plugin) :

- Dossier musique : `/media/music`
- Dossier films : `/media/movies`
- Dossier séries et animés : `/media/shows` (laissez vide si vous n'en voulez pas : les épisodes seront refusés)

Ce sont des chemins vus depuis le conteneur, pas ceux de l'hôte. Cliquez sur « Enregistrer les paramètres ».
Le reste (rangement `auto`/`flat`, extraction de pochette, autorisation des non-admins, extensions,
scan automatique) peut rester par défaut.

Si la page ne s'ouvre pas, vous pouvez écrire la config à la main dans
`jellyfin/config/plugins/configurations/Jellyfin.Plugin.MediaUploader.xml` :

```xml
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration>
  <MusicPath>/media/music</MusicPath>
  <MoviesPath>/media/movies</MoviesPath>
  <ShowsPath>/media/shows</ShowsPath>
</PluginConfiguration>
```

puis `docker compose restart jellyfin`.

L'erreur « récupération des détails du plugin depuis le dépôt » en cliquant sur la fiche du plugin est
normale : le plugin est installé à la main, il n'est dans aucun dépôt.

## 3 bis. Envoyer des fichiers (tous les utilisateurs)

La page d'upload est sur `http://IP_DU_SERVEUR:8096/MediaUploader/Ui` : tout utilisateur connecté à Jellyfin
(dans le même navigateur) peut l'utiliser, sans accès au tableau de bord. Glissez des fichiers ou des dossiers (ou « Choisir un dossier »), choisissez le type si besoin, puis **Analyser**.

1. Le serveur reçoit les fichiers (progression, reprise automatique en cas de coupure) et les analyse. **Rien n'entre encore dans la bibliothèque.**
2. L'**aperçu** regroupe les fichiers : un groupe par album, par film (avec ses sous-titres) et par série (avec ses saisons), chacun avec son dossier de destination
   et l'état *Prêt* ou *À vérifier*. Les groupes *Prêt* sont cochés d'office ; ceux à vérifier attendent que vous les corrigiez (**Modifier** pour tout le groupe,
   ✎ pour un fichier) puis les cochiez.
3. Pour les **sous-titres**, l'aperçu dit à quelle vidéo chacun est associé et sous quel nom il sera enregistré (Jellyfin exige qu'il commence par le nom de la vidéo).
4. **Confirmer et envoyer** range les fichiers cochés ; **Tout annuler** supprime le lot sans rien ranger.

Comment le plugin range les fichiers (sans que vous saisissiez quoi que ce soit) :

- **Musique** : artiste, album et pochette sont lus dans les tags du fichier → `Artiste/Album/fichier` et `cover.jpg`.
  Sans tags, les deux dossiers parents d'un dossier déposé (`Artiste/Album/01.flac`) servent ; sinon le fichier va à la racine.
- **Films** : titre et année sont lus dans le nom du fichier (`Titre (2019).mkv`, `Titre.2019.1080p.BluRay-GRP.mkv`,
  `Titre [2019]`…) → `Titre (2019)/fichier`. Sinon le dossier parent `Titre (2019)/` est essayé, puis la racine du dossier films.
- **Séries et animés** : `S01E02`, `1x02`, `Season 1 Episode 2` dans le nom, ou les dossiers `Série (2019)/Season 2/05 - Titre.mkv` d'un dossier déposé →
  `Série (2019)/Season 02/fichier`. Les animés numérotés sans saison (`[Groupe] Titre - 05 [1080p].mkv`) ne sont reconnus que si vous choisissez
  « Série / animé » (saison 1 supposée, signalée), pour ne pas prendre un film pour un épisode.
- **Sous-titres** (`.srt .ass .ssa .vtt .sub .sup`) : rattachés à la vidéo du même film ou du même épisode et renommés pour la suivre.

Tout cela est réglable : voir « Règles de détection » dans le README (Tableau de bord > Extensions > Media Uploader).

### Mettre la page dans l'accueil (plugins communautaires)

**Custom Tabs est nécessaire** pour avoir l'upload dans l'accueil : Jellyfin n'a pas d'API officielle pour ça. Les plugins communautaires **File Transformation** (dépendance) et
**Custom Tabs** ajoutent un onglet sur l'accueil dont vous fournissez le contenu HTML.

1. Si vous avez déjà modifié l'`index.html` de jellyfin-web à la main, repartez d'un fichier propre :
   `docker compose up -d --force-recreate jellyfin`
2. Tableau de bord > Extensions > Dépôts : ajoutez le dépôt de **File Transformation**
   (`https://www.iamparadox.dev/jellyfin/plugins/manifest.json`), puis installez-le depuis le catalogue.
   Il faut la version **3.0.1.0 ou plus** pour la 12.1. Si le catalogue ne la propose pas, c'est que le manifeste
   n'est pas encore à jour pour la 12 : voir le dépôt `IAmParadox27/jellyfin-plugin-file-transformation` (issues, releases).
3. Installez **Custom Tabs** pour la 12.1. La version construite pour la 12.1 que j'ai trouvée est le fork
   `stefgia/jellyfin-plugin-custom-tabs` (release 0.2.11.0, à installer via ses instructions de dépôt).
4. Redémarrez Jellyfin, puis Tableau de bord > Mes plugins > **Custom Tabs** > ajoutez un onglet « Upload » avec ce contenu :

```html
<iframe src="/MediaUploader/Ui" style="width:100%;height:85vh;border:0;"></iframe>
```

5. Ctrl+F5 sur l'accueil : l'onglet apparaît dans la barre d'onglets de l'accueil, pour tous les utilisateurs.

Si ça ne marche pas, l'URL `/MediaUploader/Ui` reste utilisable directement (favori, lien à partager).
Ces plugins sont tiers : ils suivent chacun leur propre rythme de compatibilité avec les versions de Jellyfin.

## 4. Utiliser l'API

1. Tableau de bord > Clés API > créer une clé.
2. Tester (le type, l'artiste, l'album, le titre, l'année, la saison sont déduits du fichier ; ajoutez `-F artist=... -F album=...`, `-F type=series -F title=... -F season=2` pour les forcer).
   L'API range tout de suite, sans aperçu :

```bash
curl -X POST "http://localhost:8096/MediaUploader/Upload" \
  -H "X-Emby-Token: VOTRE_CLE_API" \
  -F files=@"morceau.opus"
```

Depuis un autre conteneur du même réseau compose, l'adresse est `http://jellyfin:8096`.

## Mettre à jour le plugin après une modification du code

```bash
docker compose build plugin-builder
docker compose run --rm plugin-builder
docker compose restart jellyfin
```

## Problèmes courants

| Symptôme | Cause probable |
|---|---|
| Le plugin n'apparaît pas | Mauvaise version de Jellyfin (le plugin est compilé pour 12.1) ou pas de redémarrage. Regardez `docker compose logs jellyfin`. |
| Erreur 500 / « read-only file system » à l'upload | Volume média monté avec `:ro`, ou dossier non inscriptible pour l'utilisateur du conteneur. |
| « Le dossier séries et animés n'est pas configuré » | Renseignez le dossier séries (`/media/shows`) dans les paramètres du plugin. |
| Erreur 400 « dossier non configuré » | Chemins du plugin non renseignés (utilisez `/media/music` et `/media/movies`). |
| Un épisode est pris pour un film (ou l'inverse) | Choisissez le type « Série / animé » ou « Film » dans la page, ou corrigez le groupe dans l'aperçu ; ou ajustez les règles de détection. |
| Un sous-titre n'apparaît pas dans le lecteur | Son nom ne commence pas par celui de la vidéo : l'aperçu montre l'association proposée. Vérifiez aussi le scan de la bibliothèque. |
| « Lot inconnu ou expiré » | Le lot a dépassé sa durée de conservation ou Jellyfin a redémarré : recommencez l'analyse. |
| Envoi coupé sur un gros fichier derrière un reverse proxy | Augmentez `client_max_body_size` et les timeouts (nginx), ou les limites de votre tunnel. |
| Un film atterrit à la racine | Le nom ne contient pas d'année reconnaissable (`Titre (2019).mkv`). Corrigez-le dans l'aperçu, ajoutez une règle, ou passez `-F title=... -F year=...` à l'API. |
| Une musique atterrit à la racine | Le fichier n'a ni artiste ni album dans ses tags. Normal : Jellyfin la reconnaîtra quand même via le nom du fichier. |
| Les médias n'apparaissent pas | Lancez un scan : Tableau de bord > Bibliothèques > Scanner, ou `POST /MediaUploader/Scan`. |

## Passer de Jellyfin 10.11 à 12.1

Jellyfin ne sait pas revenir en arrière après la migration de sa base : **sauvegardez d'abord**.

```bash
docker compose stop jellyfin
tar -czf jellyfin-config-backup-$(date +%F).tar.gz jellyfin/config
```

Puis, avec ce dossier mis à jour (compose en `jellyfin/jellyfin:12.1`, plugin en .NET 10) :

```bash
docker compose build plugin-builder
docker compose run --rm plugin-builder   # remplace l'ancienne DLL (10.11) par la nouvelle
docker compose pull jellyfin
docker compose up -d
docker compose logs -f jellyfin          # attendez la fin de la migration de la base
```

Un plugin compilé pour 10.11 ne se charge pas en 12.x : la DLL doit avoir été reconstruite, d'où l'étape `plugin-builder`.
Après le démarrage, lancez un scan complet des bibliothèques.
Pour revenir en arrière : arrêter, restaurer l'archive de `jellyfin/config`, remettre l'ancienne image.

## Arrêter / supprimer

```bash
docker compose down            # arrête, garde vos données
```

Vos données sont dans `jellyfin/` et `media/` : sauvegardez ces dossiers.

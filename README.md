# Jellyfin Media Uploader

Plugin Jellyfin 12.1 (.NET 10) pour envoyer musiques, films, séries et animés dans vos bibliothèques :
- une page **/MediaUploader/Ui** ouverte à tous les utilisateurs connectés (glisser-déposer de fichiers et dossiers, progression), intégrable dans l'accueil via Custom Tabs :
  les fichiers sont d'abord **reçus et analysés par le serveur**, la page **montre où chacun sera rangé** (groupé par album, film ou série),
  vous corrigez si besoin, puis **vous confirmez** : rien n'entre dans la bibliothèque avant ;
- une **détection configurable** (expressions régulières modifiables dans les paramètres, avec un outil de test) ;
- une **API REST** utilisable par des scripts (rangement immédiat, sans confirmation).

## Prérequis : Custom Tabs

Jellyfin n'a pas d'API officielle pour ajouter une page à l'accueil. Pour que les utilisateurs accèdent à l'upload
depuis l'accueil, il faut les plugins communautaires **File Transformation** et **Custom Tabs**, qui affichent
la page du plugin dans un onglet (procédure détaillée dans [TUTO.md](TUTO.md#mettre-la-page-dans-laccueil-plugins-communautaires)).
Sans eux, la page reste accessible directement sur `/MediaUploader/Ui` (favori, lien à partager) et l'API fonctionne normalement.

## Installer

### Via le dépôt de plugins Jellyfin (recommandé)

Tableau de bord > Extensions > Dépôts > Ajouter :

```
https://raw.githubusercontent.com/Snowphey/JellyfinMediaUploader/main/manifest.json
```

Puis installer **Media Uploader** depuis le catalogue et redémarrer Jellyfin.

## Compiler

```bash
dotnet publish Jellyfin.Plugin.MediaUploader -c Release -o out
```

Copier `out/Jellyfin.Plugin.MediaUploader.dll` et `out/TagLibSharp.dll` dans :
`<dossier-données-jellyfin>/plugins/MediaUploader_1.2.0.0/` puis redémarrer Jellyfin
(supprimez les anciens dossiers `MediaUploader_1.0.0.0`, `MediaUploader_1.1.0.0` et `MediaUploader_1.1.1.0` s'il existe).

Tests du moteur de détection et du rangement (aucune dépendance à Jellyfin) : `dotnet run --project Jellyfin.Plugin.MediaUploader.Tests`.

## Import par lien et navigation (Spotify, YouTube, artistes et albums)

Un onglet **Lien** et un onglet **Parcourir** s'ajoutent à la page d'upload. Les morceaux téléchargés arrivent dans un lot, avec le même aperçu groupé par album,
les mêmes corrections et la même confirmation que les fichiers envoyés : rien n'entre dans la bibliothèque avant votre accord (sauf si l'administrateur a choisi la confirmation « seulement en cas de doute » ou « jamais »). Aucune clé d'API n'est nécessaire.

- **Lien** : playlist, album ou titre Spotify (les métadonnées sont lues sans clé sur la page d'intégration de Spotify, qui n'expose que les **100 premiers titres** d'une playlist ; chaque titre est ensuite cherché sur YouTube),
  playlist YouTube / YouTube Music, album YouTube Music ou vidéo seule (via [yt-dlp](https://github.com/yt-dlp/yt-dlp)). La liste des morceaux s'affiche : vous décochez ce que vous ne voulez pas,
  puis choisissez le rangement (chaque titre dans son album d'origine, ou tout dans un album au nom de la playlist, sous « Various Artists »).
- **Parcourir** : recherche d'un artiste, liste de ses albums / EP / singles (filtres pour live et compilations), cases à cocher, « Importer la sélection ».
  Le catalogue vient de [MusicBrainz](https://musicbrainz.org) (pistes, durées, ISRC) et des pochettes de Cover Art Archive ; chaque morceau est ensuite cherché sur YouTube (en comparant les durées) et téléchargé par yt-dlp ; le plugin écrit les tags et la pochette.

### Mise en place

1. Tableau de bord > Extensions > Media Uploader > onglet **Démarrage** : bouton *Installer / mettre à jour les outils* (**yt-dlp** et **deno** ; seuls les outils en retard sont retéléchargés)
   (téléchargés depuis leurs dépôts GitHub officiels dans le dossier de données du plugin ; empreinte SHA-256 vérifiée pour yt-dlp et deno). deno est l'exécuteur JavaScript que yt-dlp exige désormais pour YouTube.
   Vous pouvez aussi installer les outils vous-même (`pip install yt-dlp`) et indiquer leur chemin, ou les laisser dans le `PATH`.
2. ffmpeg est celui de Jellyfin. Le dossier **musique** doit être configuré (les imports ne concernent que la musique).
3. Le serveur Jellyfin doit pouvoir sortir sur Internet (YouTube, Spotify, musicbrainz.org, coverartarchive.org, github.com pour l'installation des outils).
4. Réglages : cadence des téléchargements (voir ci-dessous), format audio (M4A par défaut, sans réencodage dans la plupart des cas ; Opus ; MP3), téléchargements simultanés, fichier de cookies facultatif (YouTube Music Premium, ou si YouTube réclame une vérification).

Liste de lecture : pour un lien de **playlist**, une liste de lecture Jellyfin du même nom est créée avec les morceaux confirmés, dans l'ordre d'origine, dès que Jellyfin a scanné les nouveaux fichiers
(quelques minutes ; si la liste existe déjà, les morceaux manquants y sont ajoutés). Elle appartient à l'utilisateur qui a lancé l'import et est **publique par défaut** (visible de tous) ; une case permet de la garder privée
au moment du téléchargement, et les deux valeurs par défaut se règlent dans l'onglet Import. Une clé d'API ne peut pas posséder de liste de lecture.

Cadence (anti-robot) : pour que le serveur ne soit pas pris pour un robot par YouTube, les téléchargements sont espacés d'un délai **aléatoire de 8 à 25 s** (commun à tous les utilisateurs, puisque l'adresse IP est la même),
plafonnés à **100 par heure** et **400 par jour**, un seul à la fois, avec une pause entre les requêtes internes de yt-dlp. Si YouTube signale une limitation ou une vérification anti-robot, tout est suspendu 30 min
(puis 1 h, 2 h… jusqu'à 6 h en cas de récidive) et reprend seul, sans perdre les morceaux en attente. Tout est réglable dans les paramètres (0 = sans plafond).

Les onglets Lien et Parcourir de la page d'envoi affichent les quotas en cours (utilisés et restants cette heure et aujourd'hui, selon vos réglages) et, avant de lancer un téléchargement, prévient si la sélection dépasse un plafond (combien de morceaux attendront, et environ combien de temps).

Remarques :
- Seuls les liens Spotify, YouTube et YouTube Music sont acceptés ; l'adresse transmise aux outils est reconstruite à partir d'identifiants validés (le serveur ne sert pas de téléchargeur universel).
- Un morceau introuvable ou en échec est retiré du lot et signalé dans le suivi ; « Arrêter l'import » garde ce qui est déjà reçu.
- Ces outils dépendent de services tiers qui changent régulièrement : en cas d'erreur après quelques semaines, remettez yt-dlp à jour avec les boutons ci-dessus.
- N'importez que des contenus dont vous avez le droit de disposer.

## Configurer

Tableau de bord > Extensions > Media Uploader > onglet **Démarrage** : renseigner les trois dossiers (par exemple `/media/music`, `/media/movies` et `/media/shows`,
sur lequel vous créez dans Jellyfin une bibliothèque de type « Séries »). Les paramètres ne s'enregistrent que si les trois sont remplis.
Ce sont des dossiers de vos bibliothèques, en écriture pour l'utilisateur qui lance Jellyfin.

## Interface (tous les utilisateurs)

La page d'upload est servie sur `/MediaUploader/Ui` (connexion = session Jellyfin du navigateur).
Pour l'intégrer dans l'accueil, voir « Prérequis : Custom Tabs » ci-dessus et [TUTO.md](TUTO.md) ; sinon partagez simplement le lien.
Les administrateurs peuvent limiter l'envoi aux admins dans les paramètres.

## Envoyer : analyse, aperçu, confirmation

1. **Déposez** fichiers ou dossiers sur la page (ou « Choisir un dossier »). Le type est « Auto » par défaut ; « Série / animé » est nécessaire
   pour les animés numérotés sans saison (`[Groupe] Titre - 05 [1080p].mkv`).
2. **Analyser** : le serveur crée un *lot*, reçoit les fichiers (par morceaux, avec reprise automatique) et les analyse : tags audio, noms, chemins des dossiers.
   Les fichiers du lot sont stockés dans des `.part` cachés du dossier de la bibliothèque : Jellyfin ne les voit pas.
3. **Aperçu** : les fichiers sont **regroupés** (un album, un film avec ses sous-titres, une série avec ses saisons) et chaque groupe affiche son dossier de destination,
   un état *Prêt* ou *À vérifier*, et les remarques (« Saison 1 supposée », « Un fichier du même nom existe déjà »…).
   - cochez/décochez un **groupe** entier ; seuls les groupes *Prêt* sont cochés d'office ;
   - **Modifier** un groupe corrige le titre, l'année, l'artiste ou l'album de tous ses fichiers d'un coup (24 épisodes mal lus : une seule correction) ;
   - ✎ corrige un fichier (type, titre, année, saison, épisode, artiste, album, vidéo associée) et ✕ le retire du lot ;
   - **Tout annuler** supprime le lot : rien n'est entré dans la bibliothèque.
4. **Confirmer et envoyer** : le serveur recalcule le rangement à cet instant (un fichier a pu apparaître entre-temps) et déplace les fichiers. Les groupes non cochés restent dans le lot.

Un lot non confirmé est conservé 6 h (réglable, 1 à 72 h) puis supprimé ; il survit à un rechargement de la page mais pas à un redémarrage de Jellyfin
(les `.part` orphelins sont alors supprimés au bout de 24 h). Dans les paramètres, « Confirmation » peut être réglée sur *Toujours* (défaut),
*Seulement en cas de doute* (envoi direct si tout est reconnu avec certitude) ou *Jamais*.

### Doublons de musique

Deux morceaux sont des doublons s'ils ont le même **artiste et le même titre dans leurs tags**, comparés sans tenir compte de la casse, des accents, des espaces ni de la ponctuation
(le nom du fichier ne compte pas). La comparaison porte sur les autres fichiers du lot et sur le dossier de l'artiste dans la bibliothèque (tous albums). Un doublon est signalé
dans l'aperçu et **n'est pas enregistré**, sauf si vous cochez « Forcer l'enregistrement » (par fichier, ou « Forcer les doublons » pour un groupe). Sans titre ou sans artiste dans les tags, rien n'est détecté.
Pour l'API, `force=true` enregistre quand même. Le contrôle ne vaut que si les tags sont corrects.

### Musiques sans metadata

Les morceaux dont les tags n'ont pas d'artiste et/ou d'album (et que les règles de dossier ne complètent pas) sont réunis dans un groupe **« Musiques sans metadata »**, placé en tête,
chaque fichier indiquant ce qui manque (« sans artiste », « sans album »). Ils peuvent appartenir à des artistes ou albums différents : cochez-les **par dossier d'origine** (case du dossier)
ou **un par un**, puis **Renseigner la sélection** (artiste et/ou album ; un champ laissé vide n'est pas modifié). Les morceaux renseignés quittent le groupe et rejoignent leur album ;
recommencez avec une autre sélection pour les suivants. ✎ permet aussi de renseigner un seul morceau (et un titre, qui sert à détecter les doublons).
Les champs artiste et album proposent par **autocomplétion** les artistes et albums déjà présents dans la bibliothèque (noms de dossiers) et dans le lot, pour réutiliser un nom existant
plutôt que d'en créer une variante (« Daft punk » / « Daft Punk »).
La même autocomplétion existe partout où l'on corrige le rangement (groupe ou fichier) : artiste et album pour la musique, titre pour les films et séries (choisir un titre connu renseigne son année).

### Plusieurs artistes sur un même album

Les tags d'un album ou d'une bande originale citent souvent des artistes différents d'une piste à l'autre. Pour ne pas éclater l'album en plusieurs dossiers :
les répétitions sont retirées (« A, A, A » devient « A »), puis tous les morceaux d'un même album du lot reçoivent **un seul artiste** : le plus court s'il ouvre les autres
(« Lorien Testard » pour « Lorien Testard, Alice Duport-Percier » ; « Kendrick Lamar » pour « Kendrick Lamar feat. Drake » ou « Kendrick Lamar & Rihanna »), sinon le premier nom qu'ils partagent
(invités différents), sinon les noms présents sur toutes les pistes quel que soit leur ordre (« A, B » / « B, A » : *À vérifier*), sinon **« Various Artists »** (*À vérifier*).
Séparateurs reconnus : `, ` `; ` ` & ` ` feat. ` ` ft. ` ` featuring ` ` / `.
Quand le choix est incertain (*À vérifier*), le groupe pose la question : une liste des artistes possibles et « Confirmer ce choix », qui l'applique à tous les morceaux de l'album ; « Modifier » permet un autre nom, avec autocomplétion. Les cas sûrs ne posent aucune question.
Un artiste corrigé à la main n'est jamais modifié. Deux albums distincts qui portent le même titre (« Greatest Hits ») seraient regroupés : c'est pourquoi ce dernier cas est à vérifier.

### Rangement

| Type | Destination | Source des informations |
|---|---|---|
| Musique | `Artiste/Album/fichier` + `cover.jpg` si absente | tags (TagLibSharp), complétés par les dossiers déposés ; sinon racine |
| Film | `Titre (Année)/fichier` | nom du fichier, ou dossier parent `Titre (Année)/` ; sinon racine |
| Série / animé | `Série (Année)/Season 01/fichier` (`Season 00` pour les spéciaux) | nom du fichier (`S01E02`, `1x02`, `Season 1 Episode 2`…), complété par les dossiers déposés ; sinon racine du dossier séries |

Le nom d'origine est toujours conservé (sauf collision : suffixe `(2)`, ou écrasement si activé dans les paramètres).

### Sous-titres

Pour que Jellyfin utilise un sous-titre externe dans le lecteur, il doit être dans le dossier de la vidéo et son nom doit **commencer par le nom de la vidéo**
(`Film (2019).fr.srt`, `Film (2019).en.forced.srt`, `S01E01.ja.ass`). L'aperçu indique donc pour chaque sous-titre **à quelle vidéo il est associé**
(dans le lot, déjà dans la bibliothèque, ou choisie par vous) et **sous quel nom il sera enregistré** (`Film.2019.fr.srt` → `Film.2019.1080p.BluRay-GRP.fr.srt`).
Les cas incertains (vidéo déjà en bibliothèque, plusieurs vidéos possibles, aucune vidéo) sont marqués *À vérifier* ; vous pouvez choisir la vidéo ou garder le nom d'origine
(ou désactiver le renommage dans les paramètres). Si la vidéo est enregistrée sous un autre nom (`Film (2).mkv`), le sous-titre suit.
Extensions reconnues : `.srt .ass .ssa .vtt .sub .sup` (à ajouter dans « Extensions annexes » si votre configuration est ancienne).

## Règles de détection

Tableau de bord > Extensions > Media Uploader > **Règles de détection**. Une règle est une expression régulière .NET (insensible à la casse) à **groupes nommés** :
`title`, `year`, `season`, `episode` (films et séries), `artist`, `album` (musique). Elle porte sur le nom du fichier (sans extension, ni suffixe de langue pour un sous-titre)
ou sur le chemin d'un dossier déposé (`Série/Season 2/05 - Titre`). Les règles sont essayées **dans l'ordre**, la première qui reconnaît le fichier l'emporte ;
vous pouvez les ajouter, modifier, désactiver, réordonner, supprimer. Un film exige `title`, une série exige `episode` ; une expression invalide est refusée à l'enregistrement.
Options par règle : *seulement si le type est choisi explicitement* (évite qu'un film soit pris pour un épisode), *toujours « à vérifier »*, saison supposée.
Une expression qui met plus de 250 ms est interrompue (protection contre les expressions pathologiques).
**Tester** montre, pour des noms que vous collez (même avec des règles pas encore enregistrées), le type, le titre, la saison, l'épisode, la règle retenue et la destination.

Règles fournies : `S01E02`, `1x02`, `Season 1 Episode 2`, `[Groupe] Titre S2 - 05`, dossiers `Série/Season N/fichier`, `Titre (année)` pour les films,
`[Groupe] Titre - 05` (animé sans saison, seulement si « Série / animé » est choisi), plus deux règles désactivées (film sans année, `Artiste - Titre`).
Tant que vous ne personnalisez pas la liste, ce sont les règles du plugin, mises à jour avec lui. Une fois personnalisée, elle est à vous
(« Restaurer les règles par défaut » la remplace).

## API

Authentification : clé créée dans Tableau de bord > Clés API
(`X-Emby-Token: CLE` ou `Authorization: MediaBrowser Token="CLE"`), ou session d'un utilisateur.

| Méthode | Route | Rôle |
|---|---|---|
| GET | `/MediaUploader/Status` | État de la configuration |
| POST | `/MediaUploader/Upload` | Envoi de fichiers (multipart), rangés tout de suite |
| POST | `/MediaUploader/Upload/Start` | Démarre un envoi par morceaux (JSON), rangé tout de suite |
| PUT | `/MediaUploader/Upload/{id}/Chunk?offset=N` | Envoie un morceau (corps brut) |
| GET | `/MediaUploader/Upload/{id}` | Octets déjà reçus (reprise) |
| POST | `/MediaUploader/Upload/{id}/Complete` | Termine et range le fichier |
| DELETE | `/MediaUploader/Upload/{id}` | Abandonne l'envoi |
| POST | `/MediaUploader/Scan` | Lance un scan de bibliothèque |
| GET | `/MediaUploader/Ui` | Page d'upload |
| POST | `/MediaUploader/Batch` | Crée un lot (JSON : `mode`, `files:[{path,size}]`) et renvoie le premier plan |
| GET | `/MediaUploader/Batch/{id}` | Plan courant et avancement de la réception |
| PUT | `/MediaUploader/Batch/{id}/Overrides` | Corrections par fichier (remplacent les précédentes) ; renvoie le plan recalculé |
| PUT | `/MediaUploader/Batch/{id}/Items/{itemId}/Chunk?offset=N` | Envoie un morceau d'un fichier du lot |
| POST | `/MediaUploader/Batch/{id}/Items/{itemId}/Complete` | Termine un fichier (lit les tags) |
| DELETE | `/MediaUploader/Batch/{id}/Items/{itemId}` | Retire un fichier du lot |
| POST | `/MediaUploader/Batch/{id}/Commit` | Confirme : `{itemIds, scan}` ; range les fichiers choisis |
| DELETE | `/MediaUploader/Batch/{id}` | Annule le lot |
| GET | `/MediaUploader/Music/Suggestions?artist=` | Artistes (et albums de l'artiste) déjà présents, pour l'autocomplétion |
| GET | `/MediaUploader/Library/Titles?kind=` | Titres de films ou séries déjà présents |
| GET | `/MediaUploader/Import/Status` | Outils d'import disponibles et réglages (`?versions=true` : versions installées, administrateurs) |
| POST | `/MediaUploader/Import/Tools/{tool}` | Installe ou met à jour `yt-dlp`, `deno` ou `all` (administrateurs) |
| POST | `/MediaUploader/Import/Jobs` | Lit un lien Spotify / YouTube : `{url, layout}` ; renvoie la liste des morceaux |
| POST | `/MediaUploader/Import/Albums` | Importe des albums choisis dans la navigation (identifiants MusicBrainz) |
| GET / DELETE | `/MediaUploader/Import/Jobs/{id}` | État d'un import / arrêt |
| POST | `/MediaUploader/Import/Jobs/{id}/Start` | Lance les téléchargements : `{trackIds, layout, createPlaylist, playlistPublic}` |
| GET | `/MediaUploader/Browse/Artists?q=` · `/Browse/Artists/{id}/Albums` | Recherche d'artistes et albums (MusicBrainz) |
| GET / PUT | `/MediaUploader/Rules` | Lit / enregistre les règles de détection (administrateurs) |
| POST | `/MediaUploader/Rules/Test` | Essaie des noms de fichiers sur les règles (administrateurs) |

L'API `Upload` ne demande **aucune confirmation** : les fichiers d'une même requête sont analysés ensemble (un sous-titre est rattaché à sa vidéo) puis rangés.
Champs de `Upload` : `files` (un ou plusieurs) ; optionnels : `type` (`music`, `movie` ou `series`), `artist`, `album`,
`title`, `year`, `season` (ils remplacent ce qui est déduit du fichier), `scan` (true/false).
Les routes `Batch` sont celles de la page web ; elles sont utilisables par un script qui voudrait un aperçu.

```bash
curl -X POST "http://SERVEUR:8096/MediaUploader/Upload" \
  -H "X-Emby-Token: VOTRE_CLE_API" \
  -F files=@"01 - Titre.opus"
```

### Envoi par morceaux

L'interface web découpe chaque fichier en morceaux (8 Mo par défaut, réglable de 1 à 90 Mo dans les paramètres) :
cela contourne les limites de taille de requête des reverse proxies et tunnels, et un morceau qui échoue est
renvoyé automatiquement sans tout recommencer. Pour un script :

```bash
ID=$(curl -s -X POST "http://SERVEUR:8096/MediaUploader/Upload/Start" \
  -H "X-Emby-Token: CLE" -H "Content-Type: application/json" \
  -d '{"fileName":"film (2019).mkv","size":'$(stat -c%s "film (2019).mkv")'}' | jq -r '.UploadId')
# puis, pour chaque morceau (offset = octets déjà envoyés) :
curl -X PUT "http://SERVEUR:8096/MediaUploader/Upload/$ID/Chunk?offset=0" \
  -H "X-Emby-Token: CLE" -H "Content-Type: application/octet-stream" --data-binary @morceau0
curl -X POST "http://SERVEUR:8096/MediaUploader/Upload/$ID/Complete" -H "X-Emby-Token: CLE"
```

Un morceau doit arriver à l'offset exact attendu (sinon `409` avec l'offset du serveur). Les envois inactifs depuis
plus de 2 h sont supprimés. L'envoi simple `Upload` (multipart) reste disponible pour les petits fichiers.

Si un nom de fichier existe déjà, le plugin n'écrase rien et ajoute un suffixe (`Titre (2).opus`),
sauf si « Écraser les fichiers existants » est activé.

## Utiliser yt-dlp vous-même via l'API

L'import par lien est intégré (voir plus haut). Pour un script qui télécharge de son côté, l'API `Upload` range le résultat :
`--download-archive archive.txt` évite de retélécharger (et donc de dupliquer) un morceau déjà récupéré.

```bash
yt-dlp -x --audio-format opus --embed-metadata --embed-thumbnail -o "%(title)s [%(id)s].%(ext)s" "URL"
curl -X POST "http://SERVEUR:8096/MediaUploader/Upload" \
  -H "X-Emby-Token: CLE" -F files=@"Titre [id].opus"
```

## Publier une release (mainteneur)

Une release se fait en poussant un tag : le workflow `.github/workflows/release.yml` compile le plugin, crée la
Release GitHub avec le zip et ajoute la version à `manifest.json`. Aucune compilation locale n'est nécessaire.

Prérequis (une seule fois) : Settings > Actions > General > Workflow permissions > **Read and write permissions**.

1. (Optionnel) Vérifier que ça compile, sans installer .NET : `docker build -t mu-build ./Jellyfin.Plugin.MediaUploader`
2. Récupérer le dernier état (le workflow commite le manifest sur `main`) : `git pull`
3. Commiter et pousser les changements : `git add -A && git commit -m "..." && git push`
4. Créer le tag, **avec 4 chiffres** (format de version Jellyfin) et un numéro jamais utilisé :
   ```bash
   git tag v1.2.0.0
   git push origin v1.2.0.0
   ```
5. Suivre l'onglet **Actions** : au bout d'1 à 2 minutes, la Release apparaît avec `Jellyfin.Plugin.MediaUploader_1.2.0.0.zip`
   (le plugin + TagLibSharp), et un commit « Manifest : version 1.2.0.0 » est ajouté sur `main`.
6. `git pull` pour récupérer ce commit, et vérifier que `manifest.json` contient la nouvelle version.

Jellyfin propose ensuite la mise à jour dans Tableau de bord > Extensions > Catalogue / Mes plugins.

Si un run échoue, corriger puis supprimer et recréer le tag :
`git tag -d v1.2.0.0 && git push origin :refs/tags/v1.2.0.0`, puis refaire l'étape 4.
Si la cible change de version de Jellyfin, adapter `targetAbi` dans `Jellyfin.Plugin.MediaUploader/build.yaml` avant de tagger.

## Licence

[GPL-3.0](LICENSE) © Snowphey

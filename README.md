# Jellyfin Media Uploader

Plugin Jellyfin 12.1 (.NET 10) pour envoyer musiques et films dans vos bibliothèques :
- une page **/MediaUploader/Ui** ouverte à tous les utilisateurs connectés (glisser-déposer de fichiers et dossiers, progression), intégrable dans l'accueil via Custom Tabs ;
- une **API REST** utilisable par des scripts.

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
`<dossier-données-jellyfin>/plugins/MediaUploader_1.0.0.0/` puis redémarrer Jellyfin.

## Configurer

Tableau de bord > Extensions > Media Uploader : renseigner les dossiers musique et films
(des dossiers de vos bibliothèques, en écriture pour l'utilisateur qui lance Jellyfin).

## Interface (tous les utilisateurs)

La page d'upload est servie sur `/MediaUploader/Ui` (connexion = session Jellyfin du navigateur).
Pour l'intégrer dans l'accueil, voir « Prérequis : Custom Tabs » ci-dessus et [TUTO.md](TUTO.md) ; sinon partagez simplement le lien.
Les administrateurs peuvent limiter l'envoi aux admins dans les paramètres.

## Déduction automatique

- Musique : artiste d'album (à défaut premier interprète), album et pochette lus dans les tags (TagLibSharp)
  → `Artiste/Album/fichier` + `cover.jpg` si absente. Sans tags : racine.
- Films : titre et année déduits du nom de fichier → `Titre (Année)/fichier` (nom d'origine conservé). Sans année : racine.
- Le type (musique/film) est déduit de l'extension si `type` est omis.

## API

Authentification : clé créée dans Tableau de bord > Clés API
(`X-Emby-Token: CLE` ou `Authorization: MediaBrowser Token="CLE"`), ou session d'un utilisateur.

| Méthode | Route | Rôle |
|---|---|---|
| GET | `/MediaUploader/Status` | État de la configuration |
| POST | `/MediaUploader/Upload` | Envoi de fichiers (multipart) |
| POST | `/MediaUploader/Upload/Start` | Démarre un envoi par morceaux (JSON) |
| PUT | `/MediaUploader/Upload/{id}/Chunk?offset=N` | Envoie un morceau (corps brut) |
| GET | `/MediaUploader/Upload/{id}` | Octets déjà reçus (reprise) |
| POST | `/MediaUploader/Upload/{id}/Complete` | Termine et range le fichier |
| DELETE | `/MediaUploader/Upload/{id}` | Abandonne l'envoi |
| POST | `/MediaUploader/Scan` | Lance un scan de bibliothèque |
| GET | `/MediaUploader/Ui` | Page d'upload |

Champs de `Upload` : `files` (un ou plusieurs) ; optionnels : `type` (`music` ou `movie`), `artist`, `album`,
`title`, `year` (ils remplacent ce qui est déduit du fichier), `scan` (true/false).

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

## Brancher yt-dlp plus tard

Astuce : `--download-archive archive.txt` évite de retélécharger (et donc de dupliquer) un morceau déjà récupéré.

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
   git tag v1.0.1.0
   git push origin v1.0.1.0
   ```
5. Suivre l'onglet **Actions** : au bout d'1 à 2 minutes, la Release apparaît avec `Jellyfin.Plugin.MediaUploader_1.0.1.0.zip`
   (le plugin + TagLibSharp), et un commit « Manifest : version 1.0.1.0 » est ajouté sur `main`.
6. `git pull` pour récupérer ce commit, et vérifier que `manifest.json` contient la nouvelle version.

Jellyfin propose ensuite la mise à jour dans Tableau de bord > Extensions > Catalogue / Mes plugins.

Si un run échoue, corriger puis supprimer et recréer le tag :
`git tag -d v1.0.1.0 && git push origin :refs/tags/v1.0.1.0`, puis refaire l'étape 4.
Si la cible change de version de Jellyfin, adapter `targetAbi` dans `Jellyfin.Plugin.MediaUploader/build.yaml` avant de tagger.

## Licence

[GPL-3.0](LICENSE) © Snowphey

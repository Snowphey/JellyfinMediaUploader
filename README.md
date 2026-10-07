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
| POST | `/MediaUploader/Scan` | Lance un scan de bibliothèque |
| GET | `/MediaUploader/Ui` | Page d'upload |

Champs de `Upload` : `files` (un ou plusieurs) ; optionnels : `type` (`music` ou `movie`), `artist`, `album`,
`title`, `year` (ils remplacent ce qui est déduit du fichier), `scan` (true/false).

```bash
curl -X POST "http://SERVEUR:8096/MediaUploader/Upload" \
  -H "X-Emby-Token: VOTRE_CLE_API" \
  -F files=@"01 - Titre.opus"
```

Si un nom de fichier existe déjà, le plugin n'écrase rien et ajoute un suffixe (`Titre (2).opus`),
sauf si « Écraser les fichiers existants » est activé.

## Brancher yt-dlp plus tard

Astuce : `--download-archive archive.txt` évite de retélécharger (et donc de dupliquer) un morceau déjà récupéré.

```bash
yt-dlp -x --audio-format opus --embed-metadata --embed-thumbnail -o "%(title)s [%(id)s].%(ext)s" "URL"
curl -X POST "http://SERVEUR:8096/MediaUploader/Upload" \
  -H "X-Emby-Token: CLE" -F files=@"Titre [id].opus"
```

## Licence

[GPL-3.0](LICENSE) © Snowphey

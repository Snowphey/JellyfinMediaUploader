namespace Jellyfin.Plugin.MediaUploader.Services.Library;

/// <summary>Un des dossiers racine gérés (musique, films, séries).</summary>
/// <param name="Key">"music", "movies" ou "shows".</param>
/// <param name="Label">Nom affiché.</param>
/// <param name="Path">Chemin complet sur le serveur.</param>
public sealed record LibraryRoot(string Key, string Label, string Path);

/// <summary>Un fichier d'une liste détaillée (contenu d'un dossier supprimé ou déplacé).</summary>
/// <param name="Path">Chemin relatif à l'élément concerné.</param>
/// <param name="Size">Taille en octets.</param>
public sealed record LibraryFile(string Path, long Size);

/// <summary>Un élément (fichier ou dossier) listé dans un dossier.</summary>
/// <param name="Name">Nom.</param>
/// <param name="IsDir">Vrai pour un dossier.</param>
/// <param name="Size">Taille d'un fichier en octets (0 pour un dossier).</param>
/// <param name="ModifiedUtc">Dernière modification.</param>
/// <param name="Children">Nombre d'éléments directement dans un dossier.</param>
/// <param name="Kind">"audio", "video", "extra" (pochette, sous-titre…), "other" ou "folder".</param>
/// <param name="Hidden">Vrai pour un nom qui commence par un point (fichiers internes du plugin compris).</param>
public sealed record LibraryEntry(string Name, bool IsDir, long Size, DateTime ModifiedUtc, int Children, string Kind, bool Hidden);

/// <summary>Contenu total d'un dossier.</summary>
/// <param name="Files">Nombre de fichiers.</param>
/// <param name="Dirs">Nombre de sous-dossiers.</param>
/// <param name="Bytes">Taille totale.</param>
/// <param name="Truncated">Vrai si le parcours a été arrêté avant la fin (dossier énorme).</param>
public sealed record LibraryStats(int Files, int Dirs, long Bytes, bool Truncated);

/// <summary>Résultat d'un déplacement ou d'un renommage, ou son aperçu.</summary>
/// <param name="Source">Chemin d'origine (relatif au dossier racine).</param>
/// <param name="Target">Chemin de destination (relatif), ou null.</param>
/// <param name="Status">Aperçu : "ok", "merge", "conflict", "invalid". Exécution : "moved", "merged", "renamed", "skipped", "error".</param>
/// <param name="Message">Détail lisible.</param>
/// <param name="Files">Nombre de fichiers concernés.</param>
/// <param name="Bytes">Taille concernée.</param>
/// <param name="Listing">Fichiers contenus dans un dossier déplacé (au plus 200), pour le journal d'activité.</param>
public sealed record MoveResult(string Source, string? Target, string Status, string? Message, int Files, long Bytes, IReadOnlyList<LibraryFile>? Listing = null);

/// <summary>Résultat d'une suppression, ou son aperçu.</summary>
/// <param name="Source">Chemin supprimé (relatif au dossier racine).</param>
/// <param name="Status">Aperçu : "ok", "invalid". Exécution : "trashed", "deleted", "error".</param>
/// <param name="Message">Détail lisible.</param>
/// <param name="IsDir">Vrai pour un dossier.</param>
/// <param name="Files">Nombre de fichiers concernés.</param>
/// <param name="Dirs">Nombre de sous-dossiers concernés.</param>
/// <param name="Bytes">Taille concernée.</param>
/// <param name="TrashId">Identifiant dans la corbeille (suppression non définitive).</param>
/// <param name="Listing">Fichiers contenus (au plus 500) : arborescence montrée avant la suppression et détail du journal d'activité.</param>
/// <param name="ListingTruncated">Vrai si la liste est incomplète.</param>
public sealed record DeleteResult(string Source, string Status, string? Message, bool IsDir, int Files, int Dirs, long Bytes, string? TrashId, IReadOnlyList<LibraryFile>? Listing = null, bool ListingTruncated = false);

/// <summary>Un élément de la corbeille.</summary>
/// <param name="Id">Identifiant.</param>
/// <param name="Root">Clé du dossier racine d'origine.</param>
/// <param name="OriginalPath">Chemin d'origine (relatif au dossier racine).</param>
/// <param name="IsDir">Vrai pour un dossier.</param>
/// <param name="Files">Nombre de fichiers.</param>
/// <param name="Bytes">Taille.</param>
/// <param name="DeletedAt">Date de suppression (UTC).</param>
/// <param name="DeletedBy">Nom de l'utilisateur qui a supprimé.</param>
public sealed record TrashEntry(string Id, string Root, string OriginalPath, bool IsDir, int Files, long Bytes, DateTime DeletedAt, string DeletedBy);

/// <summary>Dossiers dont les noms ne diffèrent que par la ponctuation ou la casse (même album coupé en deux).</summary>
/// <param name="Parent">Dossier qui les contient (relatif à la racine, vide pour la racine).</param>
/// <param name="Folders">Noms des dossiers.</param>
public sealed record DuplicateAlbumGroup(string Parent, IReadOnlyList<string> Folders);

/// <summary>Anomalies repérées dans un dossier racine.</summary>
/// <param name="EmptyDirs">Dossiers sans aucun fichier.</param>
/// <param name="OrphanParts">Fichiers partiels (.part) abandonnés.</param>
/// <param name="NoCover">Dossiers dont au moins un morceau n'a ni pochette intégrée, ni image de pochette dans le dossier.</param>
/// <param name="Unknown">Fichiers dont l'extension n'est pas reconnue par le plugin.</param>
/// <param name="Truncated">Vrai si le parcours a été arrêté avant la fin.</param>
/// <param name="DuplicateAlbums">Groupes de dossiers au nom quasi identique.</param>
public sealed record HealthReport(IReadOnlyList<string> EmptyDirs, IReadOnlyList<string> OrphanParts, IReadOnlyList<string> NoCover, IReadOnlyList<string> Unknown, bool Truncated, IReadOnlyList<DuplicateAlbumGroup> DuplicateAlbums);

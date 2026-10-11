using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Tis.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tis.Seasons;

/// <summary>
/// Links movies and shows that carry one of a season's tags into that season's library folder.
/// </summary>
public class SeasonLinker
{
    private static readonly string[] MovieFolderNames = { "Movies", "Films" };
    private static readonly string[] ShowFolderNames = { "Shows", "TV Shows", "TV", "Series" };

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<SeasonLinker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonLinker"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public SeasonLinker(ILibraryManager libraryManager, IApplicationPaths applicationPaths, ILogger<SeasonLinker> logger)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Makes a library for each season that has none. Its folder lives in Jellyfin's data folder and only ever holds links.
    /// </summary>
    /// <param name="seasons">The configured seasons.</param>
    /// <returns>True when a library was made.</returns>
    public bool CreateMissingLibraries(IReadOnlyCollection<Season> seasons)
    {
        var created = false;
        var adopted = false;
        var root = Path.Combine(_applicationPaths.DataPath, "tis");
        foreach (var season in seasons.Where(s => string.IsNullOrWhiteSpace(s.LibraryId) && !string.IsNullOrWhiteSpace(s.Name)))
        {
            var name = season.Name.Trim();
            try
            {
                var existing = FindLibrary(name);
                if (existing != null && !(existing.Locations ?? Array.Empty<string>()).Any(l => IsUnder(l, root)))
                {
                    // Never take over one of the user's own libraries just because the names match.
                    _logger.LogWarning("TIS: season '{Season}' has no library, and a library with that name already exists. Pick it on the settings page or rename the season.", name);
                    continue;
                }

                if (existing == null)
                {
                    var folder = Path.Combine(root, string.Join("_", name.Split(Path.GetInvalidFileNameChars())));
                    Directory.CreateDirectory(Path.Combine(folder, MovieFolderNames[0]));
                    Directory.CreateDirectory(Path.Combine(folder, ShowFolderNames[0]));

                    // Mixed content, like a season folder with Movies and Shows in it. Made hidden when out of season,
                    // because the library can't be switched until Jellyfin has scanned it in.
                    var options = new LibraryOptions
                    {
                        Enabled = season.IsActive(DateTime.Now),
                        PathInfos = new[] { new MediaPathInfo(folder) }
                    };
                    _libraryManager.AddVirtualFolder(name, null, options, false).GetAwaiter().GetResult();
                    existing = FindLibrary(name);
                    created = true;
                    _logger.LogInformation("TIS: made library '{Library}' at '{Folder}'", name, folder);
                }

                if (existing != null)
                {
                    season.LibraryId = existing.ItemId;
                    adopted = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TIS: could not make a library for season '{Season}'", name);
            }
        }

        if (adopted)
        {
            // Remember the new libraries. This does not raise ConfigurationChanged.
            Plugin.Instance?.SaveConfiguration();
        }

        return created;
    }

    private VirtualFolderInfo? FindLibrary(string name)
    {
        return _libraryManager.GetVirtualFolders().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a folder in the season library for each tagged item, and removes the folders whose item lost its tag.
    /// The folder holds links to the item's files, plus an .nfo and artwork so the copy gets the same metadata as the original.
    /// </summary>
    /// <param name="seasons">The configured seasons.</param>
    /// <returns>True when anything was created, changed or removed.</returns>
    public bool Sync(IReadOnlyCollection<Season> seasons)
    {
        var configFolder = Path.GetDirectoryName(Plugin.Instance?.ConfigurationFilePath);
        if (string.IsNullOrEmpty(configFolder))
        {
            return false;
        }

        // Only folders listed here are ever deleted, so anything put in a season folder by hand is safe.
        var recordPath = Path.Combine(configFolder, "Jellyfin.Plugin.Tis.folders.json");
        var oldRecordPath = Path.Combine(configFolder, "Jellyfin.Plugin.Tis.links.json");
        var record = LoadRecord(recordPath);
        var oldLinks = LoadRecord(oldRecordPath);
        if (record.Count == 0 && oldLinks.Count == 0 && seasons.All(s => TagsOf(s).Length == 0))
        {
            return false;
        }

        var changed = false;

        // Versions before 1.3 linked an item's whole folder. Those links are replaced by the folders made below.
        if (oldLinks.Count > 0)
        {
            foreach (var link in oldLinks)
            {
                changed |= RemoveLink(link);
            }

            SaveRecord(oldRecordPath, new HashSet<string>());
        }

        // The media folders come from the library's settings. CollectionFolder.PhysicalLocations is no use here:
        // it starts with the library's own definition folder under /config/root.
        var libraries = _libraryManager.GetVirtualFolders();

        // Items already inside a season folder (linked copies included) are never linked again.
        var seasonFolders = new List<string>();
        var targets = new List<(Season Season, VirtualFolderInfo Library, string Folder)>();
        foreach (var season in seasons)
        {
            var library = Guid.TryParse(season.LibraryId, out var id)
                ? libraries.FirstOrDefault(l => Guid.TryParse(l.ItemId, out var libraryId) && libraryId == id)
                : null;
            var locations = library?.Locations;
            if (library == null || locations == null || locations.Length == 0)
            {
                continue;
            }

            seasonFolders.AddRange(locations.Select(TrimSeparator));
            if (TagsOf(season).Length > 0)
            {
                targets.Add((season, library, TrimSeparator(locations[0])));
            }
        }

        var wanted = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var (season, library, folder) in targets)
        {
            // One query per tag, so an item needs only one of them.
            var items = TagsOf(season).SelectMany(tag => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = KindsFor(library),
                Tags = new[] { tag },
                Recursive = true
            }));

            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Path) || seasonFolders.Any(f => IsUnder(item.Path, f)))
                {
                    continue;
                }

                if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
                {
                    // Moved or deleted since the last library scan: nothing to link to.
                    continue;
                }

                // A mixed library that keeps its movies and shows in subfolders gets its links sorted the same way.
                var destination = library.CollectionType == null
                    ? SubfolderFor(folder, item.GetBaseItemKind() == BaseItemKind.Series ? ShowFolderNames : MovieFolderNames)
                    : folder;

                wanted[Path.Combine(destination, FolderNameFor(item))] = item;
            }
        }

        foreach (var folder in record.Where(f => !wanted.ContainsKey(f)).ToList())
        {
            changed |= RemoveFolder(folder);
            record.Remove(folder);
        }

        foreach (var (folder, item) in wanted)
        {
            if (!record.Contains(folder) && (File.Exists(folder) || Directory.Exists(folder) || new FileInfo(folder).LinkTarget != null))
            {
                _logger.LogWarning("TIS: not linking '{Item}' because '{Folder}' already exists", item.Name, folder);
                continue;
            }

            try
            {
                Directory.CreateDirectory(folder);
                var isNew = record.Add(folder);
                if (Fill(folder, item))
                {
                    changed = true;
                    _logger.LogInformation("TIS: {Action} '{Item}' in '{Folder}'", isNew ? "linked" : "updated", item.Name, folder);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "TIS: could not link '{Item}' into '{Folder}'", item.Name, folder);
            }
        }

        SaveRecord(recordPath, record);
        return changed;
    }

    private static string FolderNameFor(BaseItem item)
    {
        if (Directory.Exists(item.Path))
        {
            return Path.GetFileName(TrimSeparator(item.Path));
        }

        var parent = Path.GetDirectoryName(item.Path);
        return item.IsInMixedFolder || string.IsNullOrEmpty(parent)
            ? Path.GetFileNameWithoutExtension(item.Path)
            : Path.GetFileName(parent);
    }

    // Brings the folder in line with the item: one link per file or subfolder of the original, artwork, and an .nfo.
    private static bool Fill(string folder, BaseItem item)
    {
        var isSeries = item.GetBaseItemKind() == BaseItemKind.Series;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);

        if (Directory.Exists(item.Path))
        {
            AddEntries(expected, Directory.EnumerateFileSystemEntries(item.Path));
        }
        else if (Path.GetDirectoryName(item.Path) is { Length: > 0 } parent)
        {
            if (item.IsInMixedFolder)
            {
                // Shares its folder with other movies: take the video and the files named after it (subtitles and so on).
                var stem = Path.GetFileNameWithoutExtension(item.Path);
                AddEntries(expected, Directory.EnumerateFiles(parent).Where(f =>
                    f == item.Path || Path.GetFileName(f).StartsWith(stem + ".", StringComparison.Ordinal)));
            }
            else
            {
                AddEntries(expected, Directory.EnumerateFileSystemEntries(parent));
            }
        }

        var hasNfo = expected.Keys.Any(k => k.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));
        AddImage(expected, item, ImageType.Primary, "poster");
        AddImage(expected, item, ImageType.Backdrop, "backdrop");
        AddImage(expected, item, ImageType.Logo, "logo");

        var changed = false;

        foreach (var existing in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (existing.LinkTarget != null && !(expected.TryGetValue(existing.Name, out var target) && target == existing.LinkTarget))
            {
                DeleteLink(existing.FullName);
                changed = true;
            }
        }

        foreach (var (name, target) in expected)
        {
            var path = Path.Combine(folder, name);
            if (new FileInfo(path).LinkTarget != null || File.Exists(path) || Directory.Exists(path))
            {
                continue;
            }

            if (Directory.Exists(target))
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }

            changed = true;
        }

        if (!hasNfo)
        {
            // Jellyfin reads this before looking anything up, so the copy is matched to the same title as the original.
            // In a mixed library, tvshow.nfo is also what makes a folder count as a show.
            var nfoPath = Path.Combine(folder, isSeries ? "tvshow.nfo" : "movie.nfo");
            var nfo = BuildNfo(item, isSeries);
            if (!File.Exists(nfoPath) || File.ReadAllText(nfoPath) != nfo)
            {
                File.WriteAllText(nfoPath, nfo);
                changed = true;
            }
        }

        return changed;
    }

    private static void AddEntries(Dictionary<string, string> expected, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            expected[Path.GetFileName(path)] = path;
        }
    }

    private static void AddImage(Dictionary<string, string> expected, BaseItem item, ImageType type, string stem)
    {
        if (expected.Keys.Any(k => string.Equals(Path.GetFileNameWithoutExtension(k), stem, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var image = item.GetImageInfo(type, 0);
        if (image != null && image.IsLocalFile && File.Exists(image.Path))
        {
            expected[stem + Path.GetExtension(image.Path)] = image.Path;
        }
    }

    private static string BuildNfo(BaseItem item, bool isSeries)
    {
        var root = new XElement(isSeries ? "tvshow" : "movie", new XElement("title", item.Name));

        if (!string.IsNullOrEmpty(item.OriginalTitle))
        {
            root.Add(new XElement("originaltitle", item.OriginalTitle));
        }

        if (item.ProductionYear.HasValue)
        {
            root.Add(new XElement("year", item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (item.PremiereDate.HasValue)
        {
            root.Add(new XElement("premiered", item.PremiereDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrEmpty(item.Overview))
        {
            root.Add(new XElement("plot", item.Overview));
        }

        if (!string.IsNullOrEmpty(item.OfficialRating))
        {
            root.Add(new XElement("mpaa", item.OfficialRating));
        }

        foreach (var genre in item.Genres ?? Array.Empty<string>())
        {
            root.Add(new XElement("genre", genre));
        }

        // The same element names Jellyfin's own NFO saver writes: <tmdbid>, <tvdbid>, <imdbid> (<imdb_id> for shows).
        foreach (var (provider, id) in item.ProviderIds.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var name = provider.ToLowerInvariant() + "id";
            if (string.IsNullOrEmpty(id) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                continue;
            }

            root.Add(new XElement(isSeries && name == "imdbid" ? "imdb_id" : name, id));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).ToString() + Environment.NewLine;
    }

    private static string[] TagsOf(Season season)
    {
        return season.Tag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string SubfolderFor(string folder, string[] names)
    {
        try
        {
            return Directory.EnumerateDirectories(folder)
                .FirstOrDefault(d => names.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) ?? folder;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return folder;
        }
    }

    private static BaseItemKind[] KindsFor(VirtualFolderInfo library)
    {
        return library.CollectionType switch
        {
            CollectionTypeOptions.movies => new[] { BaseItemKind.Movie },
            CollectionTypeOptions.tvshows => new[] { BaseItemKind.Series },
            _ => new[] { BaseItemKind.Movie, BaseItemKind.Series }
        };
    }

    // Removes a folder made by Fill. Links inside it are unlinked, never followed.
    private bool RemoveFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || info.LinkTarget != null)
            {
                return false;
            }

            DeleteContents(info);
            info.Delete();
            _logger.LogInformation("TIS: removed '{Folder}'", folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "TIS: could not remove '{Folder}'", folder);
            return false;
        }
    }

    private static void DeleteContents(DirectoryInfo folder)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget != null)
            {
                DeleteLink(entry.FullName);
            }
            else if (entry is DirectoryInfo subfolder)
            {
                DeleteContents(subfolder);
                subfolder.Delete();
            }
            else
            {
                entry.Delete();
            }
        }
    }

    private static void DeleteLink(string link)
    {
        try
        {
            // Removes the link itself, never what it points at.
            File.Delete(link);
        }
        catch (UnauthorizedAccessException)
        {
            // Windows refuses File.Delete on a link to a folder.
            Directory.Delete(link);
        }
    }

    private bool RemoveLink(string link)
    {
        try
        {
            if (new FileInfo(link).LinkTarget == null)
            {
                // Gone already, or replaced by something real: leave it alone.
                return false;
            }

            DeleteLink(link);
            _logger.LogInformation("TIS: removed link '{Link}'", link);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "TIS: could not remove link '{Link}'", link);
            return false;
        }
    }

    private HashSet<string> LoadRecord(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var links = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
                return new HashSet<string>(links ?? Array.Empty<string>(), StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            _logger.LogWarning(ex, "TIS: could not read '{Path}'", path);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    private void SaveRecord(string path, HashSet<string> record)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(record.OrderBy(l => l, StringComparer.Ordinal).ToArray()));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "TIS: could not write '{Path}'", path);
        }
    }

    private static string TrimSeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsUnder(string path, string folder)
    {
        return path.Equals(folder, StringComparison.Ordinal)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

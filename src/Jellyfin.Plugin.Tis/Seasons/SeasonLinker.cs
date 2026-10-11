using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Links movies and shows that carry a season's tag into that season's library folder.
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
    /// Creates the links that are missing and removes the ones whose item lost its tag.
    /// </summary>
    /// <param name="seasons">The configured seasons.</param>
    /// <returns>True when a link was created or removed.</returns>
    public bool Sync(IReadOnlyCollection<Season> seasons)
    {
        var recordPath = GetRecordPath();
        if (recordPath == null)
        {
            return false;
        }

        // Only links listed here are ever deleted, so anything put in a season folder by hand is safe.
        var record = LoadRecord(recordPath);
        if (record.Count == 0 && seasons.All(s => TagsOf(s).Length == 0))
        {
            return false;
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

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
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
                var source = SourceFor(item);
                if (source == null || seasonFolders.Any(f => IsUnder(source, f)))
                {
                    continue;
                }

                // A mixed library that keeps its movies and shows in subfolders gets its links sorted the same way.
                var destination = library.CollectionType == null
                    ? SubfolderFor(folder, item.GetBaseItemKind() == BaseItemKind.Series ? ShowFolderNames : MovieFolderNames)
                    : folder;

                wanted[Path.Combine(destination, Path.GetFileName(source))] = source;
            }
        }

        var changed = false;

        foreach (var link in record.Where(l => !wanted.ContainsKey(l)).ToList())
        {
            changed |= RemoveLink(link);
            record.Remove(link);
        }

        foreach (var (link, source) in wanted)
        {
            if (record.Contains(link) && new FileInfo(link).LinkTarget == source)
            {
                continue;
            }

            if (record.Contains(link))
            {
                // Ours, but the item moved: replace it.
                RemoveLink(link);
                record.Remove(link);
            }

            if (File.Exists(link) || Directory.Exists(link) || new FileInfo(link).LinkTarget != null)
            {
                _logger.LogWarning("TIS: not linking '{Source}' because '{Link}' already exists", source, link);
                continue;
            }

            try
            {
                if (Directory.Exists(source))
                {
                    Directory.CreateSymbolicLink(link, source);
                }
                else
                {
                    File.CreateSymbolicLink(link, source);
                }

                record.Add(link);
                changed = true;
                _logger.LogInformation("TIS: linked '{Source}' into '{Link}'", source, link);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "TIS: could not create link '{Link}'", link);
            }
        }

        SaveRecord(recordPath, record);
        return changed;
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

    // A show or a movie in its own folder is linked as the folder; a movie that shares its folder with other movies as the file.
    private static string? SourceFor(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        if (Directory.Exists(item.Path))
        {
            return TrimSeparator(item.Path);
        }

        var parent = Path.GetDirectoryName(item.Path);
        return item.IsInMixedFolder || string.IsNullOrEmpty(parent) ? item.Path : parent;
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

            _logger.LogInformation("TIS: removed link '{Link}'", link);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "TIS: could not remove link '{Link}'", link);
            return false;
        }
    }

    private static string? GetRecordPath()
    {
        var configFolder = Path.GetDirectoryName(Plugin.Instance?.ConfigurationFilePath);
        return string.IsNullOrEmpty(configFolder) ? null : Path.Combine(configFolder, "Jellyfin.Plugin.Tis.links.json");
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

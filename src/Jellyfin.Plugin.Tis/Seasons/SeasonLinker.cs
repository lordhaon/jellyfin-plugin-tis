using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Tis.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tis.Seasons;

/// <summary>
/// Links movies and shows that carry a season's tag into that season's library folder.
/// </summary>
public class SeasonLinker
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SeasonLinker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonLinker"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public SeasonLinker(ILibraryManager libraryManager, ILogger<SeasonLinker> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
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
        var tagged = seasons.Where(s => !string.IsNullOrWhiteSpace(s.Tag)).ToList();
        if (tagged.Count == 0 && record.Count == 0)
        {
            return false;
        }

        var libraryRoots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Select(TrimSeparator)
            .ToHashSet(StringComparer.Ordinal);

        // Items already inside a season folder (linked copies included) are never linked again.
        var seasonFolders = new List<string>();
        var targets = new List<(Season Season, CollectionFolder Library, string Folder)>();
        foreach (var season in seasons)
        {
            if (!Guid.TryParse(season.LibraryId, out var id) || _libraryManager.GetItemById(id) is not CollectionFolder library)
            {
                continue;
            }

            var locations = library.PhysicalLocations;
            seasonFolders.AddRange(locations.Select(TrimSeparator));
            if (!string.IsNullOrWhiteSpace(season.Tag) && locations.Length > 0)
            {
                targets.Add((season, library, TrimSeparator(locations[0])));
            }
        }

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (season, library, folder) in targets)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = KindsFor(library),
                Tags = new[] { season.Tag.Trim() },
                Recursive = true
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                var source = SourceFor(item, libraryRoots);
                if (source == null || seasonFolders.Any(f => IsUnder(source, f)))
                {
                    continue;
                }

                wanted[Path.Combine(folder, Path.GetFileName(source))] = source;
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

    private static BaseItemKind[] KindsFor(CollectionFolder library)
    {
        return library.CollectionType switch
        {
            CollectionType.movies => new[] { BaseItemKind.Movie },
            CollectionType.tvshows => new[] { BaseItemKind.Series },
            _ => new[] { BaseItemKind.Movie, BaseItemKind.Series }
        };
    }

    // A show or a movie in its own folder is linked as the folder; a movie sitting loose in a library root as the file.
    private static string? SourceFor(BaseItem item, HashSet<string> libraryRoots)
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
        return string.IsNullOrEmpty(parent) || libraryRoots.Contains(TrimSeparator(parent)) ? item.Path : parent;
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

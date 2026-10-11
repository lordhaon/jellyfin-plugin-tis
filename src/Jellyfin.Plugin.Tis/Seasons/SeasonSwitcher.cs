using System;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tis.Seasons;

/// <summary>
/// Shows the libraries that are in season and hides the ones that are not.
/// </summary>
public class SeasonSwitcher
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SeasonSwitcher> _logger;
    private readonly object _lock = new object();

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonSwitcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public SeasonSwitcher(ILibraryManager libraryManager, ILogger<SeasonSwitcher> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Sets each configured library's "Enable the library" option to match today's date.
    /// </summary>
    public void Apply()
    {
        var seasons = Plugin.Instance?.Configuration.Seasons;
        if (seasons == null || seasons.Length == 0)
        {
            return;
        }

        // The server's local time, so the container's TZ decides when midnight is.
        var today = DateTime.Now;
        var shownAny = false;

        lock (_lock)
        {
            // A library can have more than one season; it is shown while any of them is active.
            foreach (var group in seasons.GroupBy(s => s.LibraryId, StringComparer.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(group.Key, out var id) || _libraryManager.GetItemById(id) is not CollectionFolder library)
                {
                    _logger.LogWarning("TIS: season '{Season}' points at a library that no longer exists", group.First().Name);
                    continue;
                }

                var inSeason = group.Any(s => s.IsActive(today));
                var options = library.GetLibraryOptions();
                if (options.Enabled == inSeason)
                {
                    continue;
                }

                options.Enabled = inSeason;
                library.UpdateLibraryOptions(options);
                shownAny |= inSeason;
                _logger.LogInformation("TIS: {Action} library '{Library}'", inSeason ? "showing" : "hiding", library.Name);
            }
        }

        if (shownAny)
        {
            // Pick up anything added to the folder while the library was hidden.
            _libraryManager.QueueLibraryScan();
        }
    }
}

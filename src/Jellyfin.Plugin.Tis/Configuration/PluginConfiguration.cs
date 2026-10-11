using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Tis.Configuration;

/// <summary>
/// TIS settings, editable from Dashboard → Plugins → TIS.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the seasons. A library that no season points at is never touched.
    /// </summary>
    public Season[] Seasons { get; set; } = Array.Empty<Season>();
}

/// <summary>
/// A library and the part of the year it is shown in.
/// </summary>
public class Season
{
    /// <summary>
    /// Gets or sets the name shown on the settings page, e.g. Halloween.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the library to show and hide. Empty to have a library made for the season, holding only linked items.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets one or more tags, separated by commas. Movies and shows in other libraries that carry any of them
    /// are linked into this library's folder. Empty for none.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the month the season starts in (1-12).
    /// </summary>
    public int StartMonth { get; set; } = 1;

    /// <summary>
    /// Gets or sets the first day of the season.
    /// </summary>
    public int StartDay { get; set; } = 1;

    /// <summary>
    /// Gets or sets the month the season ends in (1-12).
    /// </summary>
    public int EndMonth { get; set; } = 12;

    /// <summary>
    /// Gets or sets the last day of the season. The library is still shown on this day.
    /// </summary>
    public int EndDay { get; set; } = 31;

    /// <summary>
    /// Works out whether the season covers a date. A season whose end is before its start runs over New Year.
    /// </summary>
    /// <param name="date">The date to check.</param>
    /// <returns>True when the date is inside the season.</returns>
    public bool IsActive(DateTime date)
    {
        var today = (date.Month * 100) + date.Day;
        var start = (StartMonth * 100) + StartDay;
        var end = (EndMonth * 100) + EndDay;
        return start <= end
            ? today >= start && today <= end
            : today >= start || today <= end;
    }
}

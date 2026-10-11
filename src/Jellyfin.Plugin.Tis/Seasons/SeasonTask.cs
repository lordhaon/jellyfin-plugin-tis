using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Tis.Seasons;

/// <summary>
/// Runs the season check when the server starts and just after midnight each day.
/// </summary>
public class SeasonTask : IScheduledTask
{
    private readonly SeasonSwitcher _switcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonTask"/> class.
    /// </summary>
    /// <param name="switcher">Season switcher.</param>
    public SeasonTask(SeasonSwitcher switcher)
    {
        _switcher = switcher;
    }

    /// <inheritdoc />
    public string Name => "Update seasonal libraries";

    /// <inheritdoc />
    public string Key => "TisUpdateSeasonalLibraries";

    /// <inheritdoc />
    public string Description => "Shows the libraries that are in season and hides the rest.";

    /// <inheritdoc />
    public string Category => "TIS";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _switcher.Apply();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromMinutes(1).Ticks }
        };
    }
}

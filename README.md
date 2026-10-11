# TIS – Titles In Season

A Jellyfin plugin that shows a library only during the part of the year it belongs to. Halloween movies appear in October, Christmas movies in December, and both are hidden for the rest of the year.

- Works in every Jellyfin app, because it switches the library's own **Enable the library** option on the server
- Each season has a start and end date, and can run over New Year (e.g. December 1 to January 5)
- A library can have more than one season
- Tag a movie or show in another library and it also appears in the seasonal library, without being copied
- Nothing is moved, deleted or rescanned when a library is hidden. Watched status and metadata are kept.

## Requirements

- Jellyfin **12.x**

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:
   `https://raw.githubusercontent.com/lordhaon/jellyfin-plugin-tis/main/manifest.json`
2. From **Catalog**, install **TIS - Titles In Season**.
3. Restart Jellyfin.

To install without a repository, copy `Jellyfin.Plugin.Tis.dll` into `<jellyfin data>/plugins/Tis/` and restart Jellyfin.

## Setup

1. Put each season's movies in their own folder, e.g. `Media/Seasonal/Halloween` and `Media/Seasonal/Christmas`. Keep these outside your main movie folder, or the movies will also show in the main library all year.
2. Under **Dashboard → Libraries**, add a library for each folder.
3. Under **Dashboard → Plugins → TIS**, press **Add season** for each one:

| Field | What to enter |
|---|---|
| Season name | A label for yourself, e.g. `Halloween` |
| Library | The library to show and hide |
| Tag (optional) | A tag such as `Halloween`. See below. |
| Shown from | The first day the library is shown |
| Until the end of | The last day the library is shown |

4. **Save**. The dates are applied straight away, then checked just after midnight each day and when the server starts.

## Showing movies from other libraries

Give a season a **Tag**, then add that tag to any movie or show in another library (**⋮ → Edit metadata → Tags**). At the next check the plugin creates a symbolic link to it inside the season's folder, so it shows up in the seasonal library while the file stays where it is. Remove the tag and the link is removed.

- A new tag is picked up just after midnight, or straight away if you run **Dashboard → Scheduled Tasks → TIS → Update seasonal libraries** or press Save on the settings page.
- Movies are linked into a Movies library and shows into a Shows library. For both, make two libraries with the same tag and dates.
- An item can carry several season tags.
- Jellyfin needs write access to the season's folder, and that folder must be reachable under the same paths as the rest of your media (the same Docker mount).
- The plugin only removes links it made itself. Anything you put in a season folder by hand is left alone.
- While its season is showing, a linked movie appears twice in search, once per library.

## How it works

A scheduled task (**Dashboard → Scheduled Tasks → TIS → Update seasonal libraries**) compares today's date with each season and sets the library's **Enable the library** option to match. When a library is switched back on, a library scan is queued so files added while it was hidden are picked up.

## Limitations

- Dates use the server's clock. In Docker, set the container's `TZ` variable (e.g. `TZ=America/New_York`), or seasons change at midnight UTC.
- A hidden library is hidden for everyone, administrators included.
- Removing a season from the settings leaves the library as it was. If it is hidden, switch it back on under **Dashboard → Libraries → ⋮ → Manage library → Enable the library**.
- While a season is listed, the plugin overrides any manual change to that option at the next check.

## Publishing (for the repo owner)

1. Create a GitHub repo named `jellyfin-plugin-tis` and push this folder to the `main` branch.
2. Tag a release:
   ```bash
   git tag -a v1.0.0 -m "First release"
   git push origin v1.0.0
   ```
3. The **Release** action builds the DLL, attaches `tis_1.0.0.0.zip` to a GitHub release, and adds the version to `manifest.json` on `main`.

To build locally: `dotnet build src/Jellyfin.Plugin.Tis/Jellyfin.Plugin.Tis.csproj -c Release -o out`. This needs the .NET 10 SDK.

### When Jellyfin moves to a new major version

Update `JellyfinVersion` and `TargetFramework` in the `.csproj`, and `TARGET_ABI` in `scripts/update_manifest.py`, then tag a new release.

## License

GPL-3.0

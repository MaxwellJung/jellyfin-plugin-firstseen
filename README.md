# Fix Date Added

A Jellyfin plugin that keeps a movie's or episode's original "Date added"
when Radarr or Sonarr upgrade its file, so upgrades don't jump to the top of
"Recently Added". Plex behaves this way already.

Status: in development, not ready for use.

## How it works

Jellyfin treats an upgraded file (new filename) as a new item with a new
date added. Fix Date Added records the earliest date added it has seen for
each title when Jellyfin removes or updates a movie or episode (an upgrade
removes the old item first, so that date is kept). Titles are keyed the
way Jellyfin keeps watched status across replaced files: movies by TMDb or
IMDb ID, episodes by series plus season and episode number.

When the replacement shows up with a later date, Fix Date Added sets it
back to the recorded one. Dates are stored in
`plugins/configurations/Jellyfin.Plugin.FixDateAdded.json`.

## Build and install

Requires Jellyfin 10.11 and the .NET SDK.

```bash
dotnet build -c Release
```

Copy `Jellyfin.Plugin.FixDateAdded/bin/Release/net9.0/Jellyfin.Plugin.FixDateAdded.dll`
to a `FixDateAdded` folder in Jellyfin's `plugins` folder and restart Jellyfin.

## License

[GPLv3](LICENSE)

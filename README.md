# Fix Date Added

A Jellyfin plugin that keeps a movie's or episode's original "Date added"
when Radarr or Sonarr upgrade its file, so upgrades don't jump to the top of
"Recently Added". Plex behaves this way already.

Status: in development, not ready for use.

## How it works

Jellyfin treats an upgraded file (new filename) as a new item with a new
date added. Fix Date Added records the earliest date added it has seen for
each title when Jellyfin removes or updates a movie or episode. An upgrade
removes the old item first; that date is kept, and only then is the
replacement's date restored (so a full library refresh does not rewrite
dates). Titles are keyed by every stable id Jellyfin exposes (IMDb, TMDb,
and episode series+season+episode keys), plus movie folder tags like
`{imdb-…}`.

Dates are stored in
`plugins/configurations/Jellyfin.Plugin.FixDateAdded.json`.

## Build and install

Requires Jellyfin 10.11 and the .NET SDK.

```bash
dotnet build -c Release
```

Copy `Jellyfin.Plugin.FixDateAdded/bin/Release/net9.0/Jellyfin.Plugin.FixDateAdded.dll`
to a `FixDateAdded` folder in Jellyfin's `plugins` folder and restart Jellyfin.

## Test

```bash
./tests/e2e.sh
```

Builds the plugin, runs it in a throwaway Jellyfin 10.11.10 container (no
network), fakes upgrades, and checks dates. Includes a regression for the
TMDb-vs-IMDb key mismatch (Piper-style folder move). Needs Docker and
Python 3. CI runs it on every pull request.

## License

[GPLv3](LICENSE)

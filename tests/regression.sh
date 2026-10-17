#!/usr/bin/env bash
# Hermetic regression test: build plugin, run throwaway Jellyfin (no network), fake upgrades.
# Needs Docker and Python 3.
set -euo pipefail

sdk_image=mcr.microsoft.com/dotnet/sdk:9.0@sha256:2f5eb32870b4c0074a3d5b5191a03553bcab7b74463a76a7c0a5f3ef65c90fe5
jellyfin_image=jellyfin/jellyfin:10.11.10@sha256:f66273e014b307e4ac46778845ebc1e9ee24b2e57c1fc17d5ec5ac3015649bfa

repo=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
name=fix-date-added-regression-$$
user="$(id -u):$(id -g)"
store="$work/config/plugins/configurations/Jellyfin.Plugin.FixDateAdded.json"
auth='MediaBrowser Client="regression", Device="regression", DeviceId="regression", Version="1"'

cleanup() {
    docker rm -f "$name" >/dev/null 2>&1 || true
    # Jellyfin runs as root and leaves root-owned files under $work.
    docker run --rm -v "$work:/w" --entrypoint rm "$jellyfin_image" -rf /w >/dev/null 2>&1 || true
    rm -rf "$work" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
    echo "FAIL: $*" >&2
    docker inspect -f 'state={{.State.Status}} exit={{.State.ExitCode}} error={{.State.Error}}' "$name" 2>&1 >&2 || true
    docker logs "$name" 2>&1 | tail -n 80 >&2 || true
    echo "store: $(cat "$store" 2>/dev/null || echo missing)" >&2
    exit 1
}

wait_for() {
    local what=$1
    shift
    for _ in $(seq 90); do
        "$@" && return 0
        sleep 2
    done
    fail "timed out waiting for $what"
}

jf() {
    docker exec "$name" curl -sf -H 'Content-Type: application/json' -H "Authorization: $auth" "$@"
}

items() {
    jf "http://localhost:8096/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=DateCreated,Path,ProviderIds" |
        python3 -c 'import sys, json
for i in json.load(sys.stdin)["Items"]:
    print(i["Path"].rsplit("/", 1)[1] + "\t" + i["DateCreated"])'
}

date_of() { items | awk -F'\t' -v f="$1" '$1 == f { print $2; exit }'; }
has_file() { [ -n "$(date_of "$1")" ]; }
date_is() { [ "$(date_of "$1")" = "$2" ]; }
in_store() { grep -q "\"$1\"" "$store" 2>/dev/null; }
up() {
    docker exec "$name" curl -sf http://localhost:8096/System/Info/Public |
        python3 -c 'import sys,json; json.load(sys.stdin); sys.exit(0)'
}
wizard_ready() {
    # Startup routes exist only until the wizard is finished; also confirms HTTP is usable.
    docker exec "$name" curl -sf http://localhost:8096/Startup/User >/dev/null
}
refresh() { jf -X POST http://localhost:8096/Library/Refresh; }

video() {
    docker exec "$name" mkdir -p "/media/$(dirname "$1")"
    docker exec "$name" /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error \
        -f lavfi -i color=c=black:s=320x240:d=1 -c:v libx264 "/media/$1"
}

# Move item to a new relative path (new file + delete old), for upgrades / folder changes.
replace() {
    local old=$1 new=$2
    video "$new"
    docker exec "$name" rm "/media/$old"
}

echo "Building plugin"
mkdir -p "$work/config/plugins/FixDateAdded" "$work/cache" "$work/media"
docker run --rm --user "$user" -e HOME=/tmp -v "$repo:/src:ro" -v "$work/config/plugins/FixDateAdded:/out" "$sdk_image" sh -c '
    mkdir /tmp/src && tar -C /src --exclude=.git --exclude=bin --exclude=obj -cf - . | tar -C /tmp/src -xf -
    dotnet build /tmp/src/Jellyfin.Plugin.FixDateAdded -c Release -p:RestoreLockedMode=true -o /tmp/out -v q -nologo
    cp /tmp/out/Jellyfin.Plugin.FixDateAdded.dll /out/'

echo "Starting Jellyfin"
# Run as the image user (root). Binding --user to the host uid breaks the official
# image on GitHub Actions (config init fails; wizard HTTP returns errors).
docker run -d --name "$name" --network none \
    -v "$work/config:/config" -v "$work/cache:/cache" -v "$work/media:/media" "$jellyfin_image" >/dev/null
wait_for "Jellyfin to start" up
wait_for "startup wizard" wizard_ready

jf -X POST http://localhost:8096/Startup/Configuration -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
jf -X POST http://localhost:8096/Startup/User -d '{"Name":"regression","Password":"regression"}'
jf -X POST http://localhost:8096/Startup/Complete
token=$(jf -X POST http://localhost:8096/Users/AuthenticateByName -d '{"Username":"regression","Pw":"regression"}' |
    python3 -c 'import sys, json; print(json.load(sys.stdin)["AccessToken"])')
auth="$auth, Token=\"$token\""

# Dual-id movie (TMDb + IMDb). After upgrade, move to IMDb-only folder to mimic
# replacement that only has keys[0]=IMDb while remove stored under TMDb.
dual="movies/Piper (2016) [tmdbid-399106][imdbid-tt5613056]"
imdb_only="movies/Piper (2016) [imdbid-tt5613056]"
movie="movies/Fight Club (1999) [tmdbid-550]"
show="tv/Breaking Bad [tvdbid-81189]/Season 01"
matrix="movies/The Matrix (1999) [tmdbid-603]"

echo "=== First scan"
video "$dual/Piper.720p.mkv"
video "$movie/Fight.Club.720p.mkv"
video "$show/Breaking.Bad.S01E01.720p.mkv"
jf -X POST "http://localhost:8096/Library/VirtualFolders?name=Movies&collectionType=movies&paths=/media/movies&refreshLibrary=true" -d '{}'
jf -X POST "http://localhost:8096/Library/VirtualFolders?name=Shows&collectionType=tvshows&paths=/media/tv&refreshLibrary=true" -d '{}'
wait_for "Piper in library" has_file Piper.720p.mkv
wait_for "movie recorded" in_store 550
wait_for "episode recorded" in_store 81189001001
# Dual-id item: keys[0] is TMDb when both exist.
wait_for "Piper TMDb key in store" in_store 399106
piper_date=$(date_of Piper.720p.mkv)
movie_date=$(date_of Fight.Club.720p.mkv)
episode_date=$(date_of Breaking.Bad.S01E01.720p.mkv)
echo "piper=$piper_date movie=$movie_date episode=$episode_date"
echo "store=$(cat "$store")"

echo "=== A: same-folder upgrade (populated store)"
replace "$movie/Fight.Club.720p.mkv" "$movie/Fight.Club.1080p.mkv"
replace "$show/Breaking.Bad.S01E01.720p.mkv" "$show/Breaking.Bad.S01E01.1080p.mkv"
refresh
wait_for "movie date restored" date_is Fight.Club.1080p.mkv "$movie_date"
wait_for "episode date restored" date_is Breaking.Bad.S01E01.1080p.mkv "$episode_date"
echo "PASS A"

echo "=== B: Piper upgrade into IMDb-only folder (TMDb vs IMDb key bug)"
replace "$dual/Piper.720p.mkv" "$imdb_only/Piper.1080p.mkv"
refresh
wait_for "Piper date restored after IMDb-only folder move" date_is Piper.1080p.mkv "$piper_date"
in_store tt5613056 || fail "expected IMDb key in store after Piper upgrade"
# Original date must win under IMDb key (not the upgrade scan time).
python3 -c "
import json,sys
d=json.load(open('$store'))
got=d.get('tt5613056')
want='$piper_date'
if got!=want:
    sys.exit(f'store tt5613056={got!r} want {want!r}')
"
echo "PASS B (key mismatch / Piper regression)"

echo "=== C: new title keeps its own date"
jf -X POST http://localhost:8096/System/Configuration/metadata -d '{"UseFileCreationTimeForDateAdded":false}'
video "$matrix/The.Matrix.720p.mkv"
refresh
wait_for "Matrix recorded" in_store 603
matrix_date=$(date_of The.Matrix.720p.mkv)
[[ "$matrix_date" > "$movie_date" ]] || fail "new movie date $matrix_date not after $movie_date"
echo "PASS C"

echo "=== D: upgrade after restart (empty in-memory; store on disk)"
docker restart "$name" >/dev/null
wait_for "Jellyfin to restart" up
replace "$movie/Fight.Club.1080p.mkv" "$movie/Fight.Club.2160p.mkv"
replace "$matrix/The.Matrix.720p.mkv" "$matrix/The.Matrix.1080p.mkv"
refresh
wait_for "movie date after restart" date_is Fight.Club.2160p.mkv "$movie_date"
wait_for "matrix date after restart" date_is The.Matrix.1080p.mkv "$matrix_date"
echo "PASS D"

echo "=== E: ItemRemoved path (wipe store, restart, upgrade)"
docker stop "$name" >/dev/null
# Store is root-owned; delete from a container, not the host.
docker run --rm -v "$work/config:/config" --entrypoint rm "$jellyfin_image" -f \
    /config/plugins/configurations/Jellyfin.Plugin.FixDateAdded.json
docker start "$name" >/dev/null
wait_for "Jellyfin after wipe" up
[ ! -f "$store" ] || fail "store should be empty after wipe"
replace "$imdb_only/Piper.1080p.mkv" "$imdb_only/Piper.2160p.mkv"
refresh
wait_for "Piper restored via ItemRemoved only" date_is Piper.2160p.mkv "$piper_date"
echo "PASS E"

echo "ALL PASS"

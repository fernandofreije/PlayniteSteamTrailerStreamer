# Steam Trailer Streamer

A Playnite add-on for [Extra Metadata Loader](https://github.com/darklinkpower/PlayniteExtensionsCollection/wiki/Extra-Metadata-Loader) (EML). It makes EML's "Stream Steam videos on demand" option work for trailers that use Steam's newer streaming format.

## Why

Steam's appdetails API no longer lists MP4/WebM trailer files. It only gives HLS/DASH streaming manifests (`hls_h264`, `dash_h264`, `dash_av1`).
- **Older trailers** still exist at the legacy `movie_max.mp4` URL that EML guesses, so EML keeps working for them.
- **Newer trailers** only exist as segmented streams with separate video and audio. Playnite's player (WPF `MediaElement`) can't play those.

## How it works

- At startup the extension hooks into EML's `VideoPlayerControl.UpdateGameVideoSources` with a Harmony postfix. EML and themes are not modified.
- After EML has looked for local videos and the legacy Steam MP4, the hook takes over only if no trailer was found. It then:
  1. Finds the game's HLS trailer, from EML's cached appdetails or from fresh Steam movie data (cached for 7 days).
  2. Gives EML's player a `http://127.0.0.1:<port>/trailer?...` URL that points to a small built-in server.
  3. Adds Steam's microtrailer (a plain MP4) when there's no local one, so EML's switch-source button works. It does not set `IsMicrotrailerAvailable`, because themes such as Solaris read that flag as "a local microtrailer file exists" and would hide the trailer to look for that file.
- Conversion starts as soon as the game is selected. Conversions for games you have already moved away from are cancelled.
- Movies longer than the configured limit (4 minutes by default) are skipped, since some games highlight 10+ minute gameplay videos.
- ffmpeg copies the chosen video variant and the audio into one MP4 without re-encoding (about 1–3 s for a typical trailer at 720p). The server then serves it with Range support, so seeking, looping and fullscreen work.
- Remuxed files live in `%TEMP%\SteamTrailerStreamer` with a size limit. They are deleted when Playnite starts and closes, unless "keep between sessions" is on.

## YouTube fallback

For games without a Steam trailer (Epic, GOG, Battle.net, other launchers, or Steam apps with no movies), the extension can find one on YouTube instead:

- It searches YouTube for `<game name> <release year> official trailer` with yt-dlp. Results that look like a trailer come first, and reviews, let's plays and videos over the length limit are skipped.
- yt-dlp downloads the chosen video as H.264 + AAC (the only codecs Playnite's player can play) up to the maximum quality setting, and ffmpeg merges it into one MP4. It is then served through the same local server as Steam trailers.
- If a video can't be downloaded (age-restricted, region-blocked), the next result is tried, up to three.
- The chosen video is remembered per game for 30 days. A game with no usable result isn't searched again for a day.
- yt-dlp comes from Extra Metadata Loader's settings, or from this extension's override. If EML has a YouTube cookies file set, it is used too, which helps with age-restricted trailers.

Things to know:
- **Keep yt-dlp up to date** (`yt-dlp -U`). YouTube changes often, and an outdated yt-dlp typically fails with HTTP 403 errors.
- The first view of a game takes a few seconds (search + download, about 5–10 s for a typical trailer at 720p). Later views come from the cache.
- Search can occasionally pick the wrong video.
- This downloads from YouTube, like EML's own trailer download feature. Turn it off in the settings if you don't want that.

## Requirements

- Extra Metadata Loader (tested with 1.87), with **Stream Steam videos on demand** enabled.
- ffmpeg. By default the extension uses the ffmpeg path set in EML's settings, and you can override it in this extension's settings. Without ffmpeg, only microtrailers are provided.
- Optional, for the YouTube fallback: an up-to-date yt-dlp, from EML's settings or this extension's override.

## Build

Requires the .NET SDK (8 or newer). Visual Studio isn't needed.

```powershell
./build.ps1
```

This builds `src/SteamTrailerStreamer.csproj` and packs it with Playnite's `Toolbox.exe` into `dist/*.pext`. To install, double-click the `.pext` or drag it onto Playnite.

## Releases

Releases are automated with [release-please](https://github.com/googleapis/release-please) based on [Conventional Commits](https://www.conventionalcommits.org/):

1. Merge work into `main` with Conventional Commit titles (`feat:` → minor, `fix:` → patch).
2. release-please opens or updates a "chore(main): release X.Y.Z" PR that bumps the version in `src/extension.yaml` and the csproj and updates `CHANGELOG.md`.
3. Merging that PR tags `vX.Y.Z` and creates the GitHub release. CI builds the `.pext` and attaches it to the release.

Every push and PR also runs the CI build and uploads the `.pext` as a workflow artifact.

## Settings

- Enabled
- Maximum trailer quality (360/480/720/1080). Lower quality starts faster.
- Skip videos longer than N minutes
- ffmpeg path override
- YouTube fallback on/off and yt-dlp path override
- Provide microtrailers to EML's switch-video button
- Cache limit, keep cache between sessions, and a button to clear the cache

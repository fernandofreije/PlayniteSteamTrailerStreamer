# Steam Trailer Streamer: notes for Claude

A Playnite add-on that hooks into Extra Metadata Loader (EML) with Harmony to stream
new-format Steam trailers. See README.md for how it works.

## Git conventions
- **Conventional Commits** for every commit and PR title: `feat:`, `fix:`, `perf:`,
  `refactor:`, `docs:`, `test:`, `build:`, `ci:`, `chore:`, with an optional scope
  (`feat(youtube): …`) and `!` / `BREAKING CHANGE:` for breaking changes. release-please
  derives versions and the changelog from these, so the type matters:
  `feat` → minor, `fix`/`perf` → patch, the rest → no release.
- **Linear history, no merge commits.** Work on a branch, rebase onto `main`
  (`git pull --rebase`, `git rebase main`), and merge PRs with squash or rebase. Never
  `git merge` a branch into another.
- Don't bump versions by hand. release-please updates `src/extension.yaml`,
  `src/SteamTrailerStreamer.csproj` and `CHANGELOG.md` in its release PR.

## Build
- `./build.ps1` builds `src/SteamTrailerStreamer.csproj` (.NET Framework 4.6.2, SDK-style,
  needs .NET SDK 8+) and packs `dist/*.pext` with Playnite's `Toolbox.exe`
  (`-PlaynitePath` if Playnite isn't in `%LOCALAPPDATA%\Playnite`).
- CI (`.github/workflows/build.yml`) does the same on `windows-latest` with Toolbox from the
  latest Playnite release zip.

## Code notes
- EML internals are reached by reflection in `src/EmlPatch.cs` (written against EML 1.87).
  Any new member must be added to the `missing` check so unsupported EML versions fail
  cleanly instead of throwing.
- The Harmony postfix runs on the UI thread. Never block it on network or process work; do
  that in the proxy's background jobs.
- Tool paths (ffmpeg, …) come from this extension's override setting first, then EML's
  `config.json`.

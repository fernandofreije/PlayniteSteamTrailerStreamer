using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamTrailerStreamer
{
    /// <summary>
    /// Finds a game's trailer on YouTube with yt-dlp and lets yt-dlp download it (H.264 + AAC, merged into an
    /// MP4 by ffmpeg). yt-dlp downloads itself rather than handing stream URLs to ffmpeg, because YouTube only
    /// serves those URLs with the exact client headers yt-dlp used. The chosen video id is cached per game.
    /// </summary>
    public class YoutubeTrailerDownloader
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly TimeSpan foundLifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan notFoundLifetime = TimeSpan.FromDays(1);
        private static readonly TimeSpan searchTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan downloadTimeout = TimeSpan.FromMinutes(3);
        private static readonly Regex symbolsRegex = new Regex(@"[™®©]", RegexOptions.Compiled);
        private static readonly string[] unwantedTitleWords = { "review", "reaction", "walkthrough", "let's play", "lets play", "longplay", "mod showcase" };
        private const int searchResults = 8;
        // Age-gated or region-blocked videos fail to download; try a few results before giving up.
        private const int maxDownloadAttempts = 3;

        private readonly string cacheDir;
        private readonly Func<string> ytDlpPathProvider;
        private readonly Func<string> ffmpegPathProvider;
        private readonly Func<string> cookiesPathProvider;

        public YoutubeTrailerDownloader(string cacheDir, Func<string> ytDlpPathProvider, Func<string> ffmpegPathProvider, Func<string> cookiesPathProvider)
        {
            this.cacheDir = cacheDir;
            this.ytDlpPathProvider = ytDlpPathProvider;
            this.ffmpegPathProvider = ffmpegPathProvider;
            this.cookiesPathProvider = cookiesPathProvider;
            Directory.CreateDirectory(cacheDir);
        }

        public static string BuildQuery(string gameName, int? releaseYear)
        {
            var name = symbolsRegex.Replace(gameName ?? string.Empty, string.Empty).Trim();
            return releaseYear.HasValue ? $"{name} {releaseYear} official trailer" : $"{name} official trailer";
        }

        /// <summary>True when a recent search for this game found nothing, so offering a trailer would only fail.</summary>
        public bool IsKnownMissing(string cacheKey, string query)
        {
            return TryReadCache(Path.Combine(cacheDir, $"{cacheKey}.json"), query, out var videoId) && videoId == null;
        }

        /// <param name="cacheKey">Stable per-game key (Playnite game id) for the video id cache.</param>
        public async Task DownloadAsync(string cacheKey, string query, int maxHeight, int maxSeconds, string outputPath, CancellationToken cancellationToken)
        {
            var ytDlp = ytDlpPathProvider() ?? throw new InvalidOperationException("yt-dlp was not found.");
            var ffmpeg = ffmpegPathProvider() ?? throw new InvalidOperationException("ffmpeg was not found.");
            var cachePath = Path.Combine(cacheDir, $"{cacheKey}.json");

            var tried = new HashSet<string>();
            if (TryReadCache(cachePath, query, out var cachedId))
            {
                if (cachedId == null)
                {
                    throw new InvalidOperationException($"No YouTube trailer for \"{query}\" (cached).");
                }

                tried.Add(cachedId);
                if (await TryDownloadAsync(ytDlp, ffmpeg, cachedId, maxHeight, outputPath, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            var output = await RunYtDlpAsync(ytDlp,
                $"--flat-playlist --print \"%(id)s|%(duration)s|%(title)s\" \"ytsearch{searchResults}:{query.Replace("\"", string.Empty)}\"",
                searchTimeout, cancellationToken).ConfigureAwait(false);

            foreach (var videoId in RankResults(SplitLines(output), maxSeconds).Where(id => !tried.Contains(id)).Take(maxDownloadAttempts))
            {
                if (await TryDownloadAsync(ytDlp, ffmpeg, videoId, maxHeight, outputPath, cancellationToken).ConfigureAwait(false))
                {
                    WriteCache(cachePath, query, videoId);
                    return;
                }
            }

            WriteCache(cachePath, query, null);
            throw new InvalidOperationException($"No downloadable YouTube trailer found for \"{query}\".");
        }

        /// <summary>
        /// Results within the length limit, trailers first, keeping YouTube's order otherwise.
        /// Reviews, let's plays and similar are dropped.
        /// </summary>
        internal static List<string> RankResults(IEnumerable<string> lines, int maxSeconds)
        {
            return lines
                .Select(line => line.Split(new[] { '|' }, 3))
                .Where(parts => parts.Length == 3 && parts[0].Length > 0)
                .Select(parts => new { Id = parts[0], Duration = ParseDouble(parts[1]), Title = parts[2].ToLowerInvariant() })
                .Where(r => r.Duration <= maxSeconds) // 0 = unknown duration, allowed
                .Where(r => !unwantedTitleWords.Any(word => r.Title.Contains(word)))
                .OrderBy(r => r.Title.Contains("trailer") ? 0 : 1) // stable
                .Select(r => r.Id)
                .ToList();
        }

        private async Task<bool> TryDownloadAsync(string ytDlp, string ffmpeg, string videoId, int maxHeight, string outputPath, CancellationToken cancellationToken)
        {
            // "<id>.mp4.part.*" is skipped by the cache trimmer and removed as a leftover on startup.
            var workPath = outputPath + ".part.mp4";
            // MediaElement only plays H.264/AAC, so never pick VP9/AV1/Opus.
            var format = $"bv*[vcodec^=avc1][height<={maxHeight}]+ba[ext=m4a]/b[ext=mp4][vcodec^=avc1][height<={maxHeight}]";
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await RunYtDlpAsync(ytDlp,
                    $"-f \"{format}\" --merge-output-format mp4 --ffmpeg-location \"{ffmpeg}\" --no-part --no-mtime --force-overwrites --quiet --no-progress -o \"{workPath}\" -- {videoId}",
                    downloadTimeout, cancellationToken).ConfigureAwait(false);

                if (!File.Exists(workPath))
                {
                    throw new InvalidOperationException("yt-dlp finished without writing the video.");
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
                File.Move(workPath, outputPath);
                logger.Info($"Downloaded YouTube trailer {videoId} in {stopwatch.ElapsedMilliseconds} ms");
                return true;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                logger.Warn($"Could not download YouTube video {videoId}: {e.Message.Trim()}");
                return false;
            }
            finally
            {
                DeleteWorkFiles(outputPath);
            }
        }

        private Task<string> RunYtDlpAsync(string ytDlp, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var common = "--no-warnings --no-playlist --encoding utf-8";
            var cookies = cookiesPathProvider();
            if (cookies != null)
            {
                common += $" --cookies \"{cookies}\"";
            }
            return ProcessRunner.RunAsync(ytDlp, $"{common} {arguments}", timeout, cancellationToken);
        }

        private static void DeleteWorkFiles(string outputPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                foreach (var file in Directory.GetFiles(dir, Path.GetFileName(outputPath) + ".part.*"))
                {
                    File.Delete(file);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        private static bool TryReadCache(string path, string query, out string videoId)
        {
            videoId = null;
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                var json = JObject.Parse(File.ReadAllText(path));
                if ((string)json["Query"] != query)
                {
                    // Game was renamed or its release date changed: search again.
                    return false;
                }

                var id = (string)json["VideoId"];
                var lifetime = string.IsNullOrEmpty(id) ? notFoundLifetime : foundLifetime;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= lifetime)
                {
                    return false;
                }

                videoId = string.IsNullOrEmpty(id) ? null : id;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        private static void WriteCache(string path, string query, string videoId)
        {
            try
            {
                File.WriteAllText(path, new JObject { ["Query"] = query, ["VideoId"] = videoId ?? string.Empty }.ToString());
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Warn($"Could not cache YouTube result for \"{query}\": {e.Message}");
            }
        }

        private static List<string> SplitLines(string text)
        {
            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        private static double ParseDouble(string text)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
    }
}

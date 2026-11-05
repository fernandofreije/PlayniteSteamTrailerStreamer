using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamTrailerStreamer
{
    /// <summary>A Steam trailer as separate video and audio stream URLs (AudioUrl is null for a single muxed stream).</summary>
    public class TrailerStreams
    {
        /// <summary>The HLS master playlist the streams came from, for logging.</summary>
        public string SourceUrl { get; set; }
        public string VideoUrl { get; set; }
        public string AudioUrl { get; set; }
        public int Height { get; set; }
        public double DurationSeconds { get; set; }
    }

    /// <summary>
    /// Turns a trailer with separate video and audio streams (Steam HLS) into a single progressive MP4
    /// that WPF's MediaElement can play. Streams are copied, never re-encoded.
    /// </summary>
    public class FfmpegRemuxer
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly Regex resolutionRegex = new Regex(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.Compiled);
        private static readonly Regex audioUriRegex = new Regex(@"TYPE=AUDIO.*?URI=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex extinfRegex = new Regex(@"^#EXTINF:([\d.]+)", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly TimeSpan processTimeout = TimeSpan.FromMinutes(3);

        private readonly Func<string> ffmpegPathProvider;

        public FfmpegRemuxer(Func<string> ffmpegPathProvider)
        {
            this.ffmpegPathProvider = ffmpegPathProvider;
        }

        /// <summary>
        /// Returns the first movie no longer than <paramref name="maxSeconds"/>, or the shortest one when all are longer.
        /// Some games highlight 10+ minute gameplay videos, which would take far too long to fetch.
        /// </summary>
        public async Task<TrailerStreams> SelectAsync(IList<string> masterUrls, int maxHeight, int maxSeconds, CancellationToken cancellationToken)
        {
            TrailerStreams shortest = null;
            foreach (var masterUrl in masterUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trailer = await InspectAsync(masterUrl, maxHeight, cancellationToken).ConfigureAwait(false);
                if (trailer == null)
                {
                    continue;
                }

                if (trailer.DurationSeconds <= 0 || trailer.DurationSeconds <= maxSeconds)
                {
                    return trailer;
                }

                logger.Debug($"Skipping {trailer.DurationSeconds:0}s Steam movie {masterUrl}");
                if (shortest == null || trailer.DurationSeconds < shortest.DurationSeconds)
                {
                    shortest = trailer;
                }
            }

            return shortest;
        }

        /// <summary>Picks the Steam movie to use (see <see cref="SelectAsync"/>) and remuxes it to <paramref name="outputPath"/>.</summary>
        public async Task ProduceSteamTrailerAsync(IList<string> masterUrls, int maxHeight, int maxSeconds, string outputPath, CancellationToken cancellationToken)
        {
            var trailer = await SelectAsync(masterUrls, maxHeight, maxSeconds, cancellationToken).ConfigureAwait(false);
            if (trailer == null)
            {
                throw new InvalidOperationException("No playable Steam trailer found.");
            }
            await RemuxAsync(trailer, outputPath, cancellationToken).ConfigureAwait(false);
        }

        public async Task RemuxAsync(TrailerStreams trailer, string outputPath, CancellationToken cancellationToken)
        {
            var ffmpegPath = ffmpegPathProvider();
            if (ffmpegPath == null)
            {
                throw new InvalidOperationException("ffmpeg was not found.");
            }

            var inputArgs = trailer.AudioUrl != null
                ? $"-i \"{trailer.VideoUrl}\" -i \"{trailer.AudioUrl}\" -map 0:v:0 -map 1:a:0"
                : $"-i \"{trailer.VideoUrl}\" -map 0:v:0 -map 0:a?";
            var tempPath = outputPath + ".part";
            var args = $"-hide_banner -loglevel error -y {inputArgs} -c copy -movflags +faststart -f mp4 \"{tempPath}\"";

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await ProcessRunner.RunAsync(ffmpegPath, args, processTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            TryDelete(outputPath);
            File.Move(tempPath, outputPath);
            logger.Info($"Remuxed {trailer.DurationSeconds:0}s {trailer.Height}p trailer in {stopwatch.ElapsedMilliseconds} ms: {trailer.SourceUrl}");
        }

        /// <summary>
        /// Reads the master playlist to pick the best video variant within the height limit plus the audio
        /// playlist (so ffmpeg only downloads what it needs), and sums the variant's segment durations.
        /// </summary>
        private static async Task<TrailerStreams> InspectAsync(string masterUrl, int maxHeight, CancellationToken cancellationToken)
        {
            try
            {
                var master = await GetStringAsync(masterUrl, cancellationToken).ConfigureAwait(false);
                var baseUri = new Uri(masterUrl);
                var lines = master.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var variants = new List<KeyValuePair<int, string>>();
                string audioUri = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    var audioMatch = audioUriRegex.Match(lines[i]);
                    if (audioMatch.Success && audioUri == null)
                    {
                        audioUri = new Uri(baseUri, audioMatch.Groups[1].Value).ToString();
                    }

                    if (lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal) && i + 1 < lines.Length)
                    {
                        var resolution = resolutionRegex.Match(lines[i]);
                        var height = resolution.Success ? int.Parse(resolution.Groups[2].Value) : 0;
                        variants.Add(new KeyValuePair<int, string>(height, new Uri(baseUri, lines[i + 1].Trim()).ToString()));
                    }
                }

                if (variants.Count == 0)
                {
                    return null;
                }

                var video = variants.Where(v => v.Key <= maxHeight).OrderByDescending(v => v.Key).Cast<KeyValuePair<int, string>?>().FirstOrDefault()
                    ?? variants.OrderBy(v => v.Key).First();

                var duration = 0.0;
                try
                {
                    var playlist = await GetStringAsync(video.Value, cancellationToken).ConfigureAwait(false);
                    duration = extinfRegex.Matches(playlist).Cast<Match>()
                        .Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    logger.Warn($"Could not read HLS video playlist {video.Value}: {e.Message}");
                }

                return new TrailerStreams
                {
                    SourceUrl = masterUrl,
                    VideoUrl = video.Value,
                    AudioUrl = audioUri,
                    Height = video.Key,
                    DurationSeconds = duration
                };
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                logger.Warn($"Could not read HLS master playlist {masterUrl}: {e.Message}");
                return null;
            }
        }

        private static async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            using (var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }
}

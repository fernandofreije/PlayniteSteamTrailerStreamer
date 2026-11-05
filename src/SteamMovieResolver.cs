using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace SteamTrailerStreamer
{
    public class SteamMovie
    {
        /// <summary>HLS master playlists of all the app's movies, highlighted ones first.</summary>
        public List<string> HlsUrls { get; set; }
        public string MicrotrailerUrl { get; set; }
    }

    /// <summary>
    /// Finds the new-format (HLS) trailer for a Steam app. Uses Extra Metadata Loader's cached appdetails
    /// when it already contains the new fields, otherwise fetches fresh movie data from Steam once and caches it.
    /// </summary>
    public class SteamMovieResolver
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly TimeSpan cacheLifetime = TimeSpan.FromDays(7);

        private readonly string cacheDir;
        // A null value means "checked, nothing usable" so we don't hit Steam again this session.
        private readonly ConcurrentDictionary<string, SteamMovie> resolved = new ConcurrentDictionary<string, SteamMovie>();

        public SteamMovieResolver(string cacheDir)
        {
            this.cacheDir = cacheDir;
            Directory.CreateDirectory(cacheDir);
        }

        public SteamMovie Resolve(string appId, string emlAppDetailsPath)
        {
            if (resolved.TryGetValue(appId, out var known))
            {
                return known;
            }

            var movie = ParseMovie(ReadFileOrNull(emlAppDetailsPath));
            if (movie == null)
            {
                var ownCachePath = Path.Combine(cacheDir, $"{appId}.json");
                var json = File.Exists(ownCachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(ownCachePath) < cacheLifetime
                    ? ReadFileOrNull(ownCachePath)
                    : DownloadMovieData(appId, ownCachePath);
                movie = ParseMovie(json);
            }

            resolved[appId] = movie;
            return movie;
        }

        private static string DownloadMovieData(string appId, string savePath)
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=movies";
            try
            {
                // Run off the caller's synchronization context: this is invoked from the UI thread.
                var json = Task.Run(() => httpClient.GetStringAsync(url)).GetAwaiter().GetResult();
                File.WriteAllText(savePath, json);
                return json;
            }
            catch (Exception e)
            {
                logger.Warn($"Failed to download Steam movie data for app {appId}: {e.Message}");
                return null;
            }
        }

        internal static SteamMovie ParseMovie(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(json).Properties().FirstOrDefault()?.Value as JObject;
                // "data" is an empty array instead of an object when the app has no movies.
                var movies = (root?["data"] as JObject)?["movies"] as JArray;
                var candidates = movies?.OfType<JObject>().Where(m => !string.IsNullOrEmpty((string)m["hls_h264"])).ToList();
                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                // Stable sort keeps Steam's order within highlighted and non-highlighted movies.
                var ordered = candidates.OrderBy(m => (bool?)m["highlight"] == true ? 0 : 1).Select(m => (string)m["hls_h264"]).ToList();
                return new SteamMovie
                {
                    HlsUrls = ordered,
                    MicrotrailerUrl = BuildMicrotrailerUrl(ordered[0])
                };
            }
            catch (Exception e)
            {
                logger.Warn($"Failed to parse Steam movie data: {e.Message}");
                return null;
            }
        }

        private static string BuildMicrotrailerUrl(string hlsUrl)
        {
            // https://video.akamai.steamstatic.com/store_trailers/{app}/{id}/{hash}/{ts}/hls_264_master.m3u8?t=...
            // The microtrailer lives next to the manifest. MediaElement crashes on Steam's https, so use http
            // (same workaround Extra Metadata Loader applies to its own Steam URLs).
            var uri = new Uri(hlsUrl);
            var basePath = uri.AbsolutePath.Substring(0, uri.AbsolutePath.LastIndexOf('/') + 1);
            return new UriBuilder(Uri.UriSchemeHttp, uri.Host) { Path = basePath + "microtrailer.mp4" }.Uri.ToString();
        }

        private static string ReadFileOrNull(string path)
        {
            try
            {
                return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}

using Playnite.SDK;
using Playnite.SDK.Data;
using System.Collections.Generic;

namespace SteamTrailerStreamer
{
    public class SteamTrailerStreamerSettings : ObservableObject
    {
        private bool enabled = true;
        public bool Enabled { get => enabled; set => SetValue(ref enabled, value); }

        private int maxVideoHeight = 720;
        /// <summary>Highest trailer resolution to stream (360, 480, 720 or 1080).</summary>
        public int MaxVideoHeight { get => maxVideoHeight; set => SetValue(ref maxVideoHeight, value); }

        private int maxTrailerMinutes = 4;
        /// <summary>Movies longer than this are skipped in favour of a shorter one (e.g. 10+ minute gameplay videos).</summary>
        public int MaxTrailerMinutes { get => maxTrailerMinutes; set => SetValue(ref maxTrailerMinutes, value); }

        private bool keepCacheBetweenSessions = false;
        public bool KeepCacheBetweenSessions { get => keepCacheBetweenSessions; set => SetValue(ref keepCacheBetweenSessions, value); }

        private string ffmpegPath = string.Empty;
        /// <summary>Optional override. When empty, Extra Metadata Loader's ffmpeg path is used.</summary>
        public string FfmpegPath { get => ffmpegPath; set => SetValue(ref ffmpegPath, value); }

        private bool youtubeFallback = true;
        /// <summary>Search YouTube (with yt-dlp) when there is no Steam trailer, e.g. for Epic, GOG or Battle.net games.</summary>
        public bool YoutubeFallback { get => youtubeFallback; set => SetValue(ref youtubeFallback, value); }

        private string ytDlpPath = string.Empty;
        /// <summary>Optional override. When empty, Extra Metadata Loader's yt-dlp path is used.</summary>
        public string YtDlpPath { get => ytDlpPath; set => SetValue(ref ytDlpPath, value); }

        private bool provideMicrotrailers = true;
        public bool ProvideMicrotrailers { get => provideMicrotrailers; set => SetValue(ref provideMicrotrailers, value); }

        private int cacheLimitMb = 500;
        public int CacheLimitMb { get => cacheLimitMb; set => SetValue(ref cacheLimitMb, value); }
    }

    public class SteamTrailerStreamerSettingsViewModel : ObservableObject, ISettings
    {
        private readonly SteamTrailerStreamer plugin;
        private SteamTrailerStreamerSettings editingClone;

        private SteamTrailerStreamerSettings settings;
        public SteamTrailerStreamerSettings Settings { get => settings; set => SetValue(ref settings, value); }

        [DontSerialize]
        public List<int> QualityOptions { get; } = new List<int> { 360, 480, 720, 1080 };

        [DontSerialize]
        public string DetectedFfmpegPath => plugin.ResolveFfmpegPath() ?? "Not found";

        [DontSerialize]
        public string DetectedYtDlpPath => plugin.ResolveYtDlpPath() ?? "Not found";

        [DontSerialize]
        public RelayCommand ClearCacheCommand => new RelayCommand(() =>
        {
            plugin.Cache?.Clear();
            plugin.PlayniteApi.Dialogs.ShowMessage("Trailer cache cleared.", "Steam Trailer Streamer");
        });

        public SteamTrailerStreamerSettingsViewModel(SteamTrailerStreamer plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<SteamTrailerStreamerSettings>() ?? new SteamTrailerStreamerSettings();
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
            plugin.Cache?.SetLimit(Settings.CacheLimitMb);
            OnPropertyChanged(nameof(DetectedFfmpegPath));
            OnPropertyChanged(nameof(DetectedYtDlpPath));
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (Settings.CacheLimitMb < 50)
            {
                errors.Add("Cache limit must be at least 50 MB.");
            }
            if (Settings.MaxTrailerMinutes < 1)
            {
                errors.Add("Maximum trailer length must be at least 1 minute.");
            }
            return errors.Count == 0;
        }
    }
}

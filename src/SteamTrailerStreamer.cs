using HarmonyLib;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Windows.Controls;

namespace SteamTrailerStreamer
{
    public class SteamTrailerStreamer : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string emlPluginId = "705fdbca-e1fc-4004-b839-1d040b8b4429";

        internal static SteamTrailerStreamer Instance { get; private set; }

        private Harmony harmony;

        public SteamTrailerStreamerSettingsViewModel SettingsViewModel { get; }
        internal TrailerCache Cache { get; private set; }
        internal TrailerProxyServer Server { get; private set; }
        internal SteamMovieResolver Resolver { get; private set; }

        public override Guid Id { get; } = Guid.Parse("accc7230-a738-400d-82ef-9e363d51219a");

        public SteamTrailerStreamer(IPlayniteAPI api) : base(api)
        {
            Instance = this;
            SettingsViewModel = new SteamTrailerStreamerSettingsViewModel(this);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            try
            {
                Cache = new TrailerCache(Path.Combine(Path.GetTempPath(), "SteamTrailerStreamer"), SettingsViewModel.Settings.CacheLimitMb);
                ClearCacheUnlessKept();
                Resolver = new SteamMovieResolver(Path.Combine(GetPluginUserDataPath(), "appdetails"));
                Server = new TrailerProxyServer(Cache, new FfmpegRemuxer(ResolveFfmpegPath));
                Server.Start();
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to start Steam Trailer Streamer");
                Notify($"Steam Trailer Streamer could not start: {e.Message}");
                return;
            }

            harmony = new Harmony(Id.ToString());
            if (EmlPatch.TryApply(harmony, out var error))
            {
                logger.Info("Hooked into Extra Metadata Loader's video player");
            }
            else
            {
                logger.Warn(error);
                Notify($"Steam Trailer Streamer is inactive: {error}");
            }

            if (ResolveFfmpegPath() == null)
            {
                Notify("Steam Trailer Streamer: ffmpeg was not found, so only Steam microtrailers can be streamed. Set the ffmpeg path in Extra Metadata Loader or in this extension's settings.");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            harmony?.UnpatchAll(Id.ToString());
            Server?.Stop();
            ClearCacheUnlessKept();
        }

        private void ClearCacheUnlessKept()
        {
            if (SettingsViewModel.Settings.KeepCacheBetweenSessions)
            {
                Cache?.DeletePartialFiles();
            }
            else
            {
                Cache?.Clear();
            }
        }

        /// <summary>Our override if set, otherwise Extra Metadata Loader's configured ffmpeg.</summary>
        internal string ResolveFfmpegPath()
        {
            var overridePath = SettingsViewModel.Settings.FfmpegPath;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return File.Exists(overridePath) ? overridePath : null;
            }

            try
            {
                var emlConfig = Path.Combine(PlayniteApi.Paths.ExtensionsDataPath, emlPluginId, "config.json");
                if (File.Exists(emlConfig))
                {
                    var path = (string)JObject.Parse(File.ReadAllText(emlConfig))["FfmpegPath"];
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn($"Could not read Extra Metadata Loader settings: {e.Message}");
            }

            return null;
        }

        private void Notify(string message)
        {
            PlayniteApi.Notifications.Add(new NotificationMessage($"{Id}-{message.GetHashCode()}", message, NotificationType.Info));
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return SettingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SteamTrailerStreamerSettingsView();
        }
    }
}

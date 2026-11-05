using HarmonyLib;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SteamTrailerStreamer
{
    /// <summary>
    /// Harmony postfix on Extra Metadata Loader's VideoPlayerControl.UpdateGameVideoSources.
    /// EML first tries local files and its legacy Steam MP4 guess; only when that leaves no trailer
    /// do we supply the new-format Steam trailer (through the local proxy) and the Steam microtrailer,
    /// or, when Steam has nothing, a trailer found on YouTube.
    /// Written against Extra Metadata Loader 1.87.
    /// </summary>
    internal static class EmlPatch
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static FieldInfo trailerVideoPathField;
        private static FieldInfo microVideoPathField;
        private static FieldInfo multipleSourcesAvailableField;
        private static FieldInfo useMicrovideosSourceField;
        private static FieldInfo activeVideoTypeField;
        private static FieldInfo pluginDataPathField;
        private static FieldInfo currentGameField;
        private static PropertyInfo settingsModelProperty;
        private static PropertyInfo videoSourceProperty;
        private static MethodInfo getSteamIdMethod;
        private static bool loggedPostfixError;

        public static bool TryApply(Harmony harmony, out string error)
        {
            var emlAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ExtraMetadataLoader");
            if (emlAssembly == null)
            {
                error = "Extra Metadata Loader is not installed or not enabled.";
                return false;
            }

            var controlType = emlAssembly.GetType("ExtraMetadataLoader.VideoPlayerControl");
            var target = controlType?.GetMethod("UpdateGameVideoSources", instanceFlags, null, Type.EmptyTypes, null);

            trailerVideoPathField = controlType?.GetField("trailerVideoPath", instanceFlags);
            microVideoPathField = controlType?.GetField("microVideoPath", instanceFlags);
            multipleSourcesAvailableField = controlType?.GetField("multipleSourcesAvailable", instanceFlags);
            useMicrovideosSourceField = controlType?.GetField("useMicrovideosSource", instanceFlags);
            activeVideoTypeField = controlType?.GetField("activeVideoType", instanceFlags);
            pluginDataPathField = controlType?.GetField("pluginDataPath", instanceFlags);
            currentGameField = controlType?.GetField("currentGame", instanceFlags);
            settingsModelProperty = controlType?.GetProperty("SettingsModel", instanceFlags);
            videoSourceProperty = controlType?.GetProperty("VideoSource", instanceFlags);
            getSteamIdMethod = controlType?.GetMethod("GetSteamId", instanceFlags, null, new[] { typeof(Game) }, null);

            var missing = new Dictionary<string, MemberInfo>
            {
                ["UpdateGameVideoSources"] = target,
                ["trailerVideoPath"] = trailerVideoPathField,
                ["microVideoPath"] = microVideoPathField,
                ["multipleSourcesAvailable"] = multipleSourcesAvailableField,
                ["useMicrovideosSource"] = useMicrovideosSourceField,
                ["activeVideoType"] = activeVideoTypeField,
                ["pluginDataPath"] = pluginDataPathField,
                ["currentGame"] = currentGameField,
                ["SettingsModel"] = settingsModelProperty,
                ["VideoSource"] = videoSourceProperty,
                ["GetSteamId"] = getSteamIdMethod
            }.Where(m => m.Value == null).Select(m => m.Key).ToList();

            if (controlType == null || missing.Count > 0)
            {
                error = controlType == null
                    ? "Extra Metadata Loader's video player was not found."
                    : $"This Extra Metadata Loader version is not supported (missing: {string.Join(", ", missing)}).";
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(typeof(EmlPatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
            error = null;
            return true;
        }

        private static void Postfix(object __instance)
        {
            try
            {
                Apply(__instance);
            }
            catch (Exception e)
            {
                // Never break EML's player; log the first failure only to avoid flooding the log.
                if (!loggedPostfixError)
                {
                    loggedPostfixError = true;
                    logger.Error(e, "Steam Trailer Streamer failed to provide a video source");
                }
            }
        }

        private static void Apply(object control)
        {
            var plugin = SteamTrailerStreamer.Instance;
            var settings = plugin?.SettingsViewModel.Settings;
            if (settings == null || !settings.Enabled || plugin.Server == null)
            {
                return;
            }

            var emlSettings = GetProperty(settingsModelProperty.GetValue(control), "Settings");
            if (emlSettings == null || !(GetProperty(emlSettings, "StreamSteamVideosOnDemand") is bool streamEnabled) || !streamEnabled)
            {
                return;
            }

            if (trailerVideoPathField.GetValue(control) != null)
            {
                // EML found a local trailer or a legacy Steam MP4.
                return;
            }

            if (!(currentGameField.GetValue(control) is Game game))
            {
                return;
            }

            var hasFfmpeg = plugin.ResolveFfmpegPath() != null;
            var maxHeight = settings.MaxVideoHeight;
            var maxSeconds = settings.MaxTrailerMinutes * 60;
            Uri trailer = null;
            var micro = microVideoPathField.GetValue(control) as Uri;
            var providedMicro = false;

            var steamId = getSteamIdMethod.Invoke(control, new object[] { game }) as string;
            var movie = string.IsNullOrEmpty(steamId)
                ? null
                : plugin.Resolver.Resolve(steamId, Path.Combine((string)pluginDataPathField.GetValue(control), $"{game.Id}_SteamAppDetails.json"));
            if (movie != null)
            {
                // Prepare starts converting right away, before the player asks for the file.
                if (hasFfmpeg)
                {
                    var hlsUrls = movie.HlsUrls;
                    var remuxer = plugin.Remuxer;
                    trailer = plugin.Server.Prepare($"steam|{steamId}|{maxHeight}|{maxSeconds}|{hlsUrls[0]}", hlsUrls[0],
                        (path, ct) => remuxer.ProduceSteamTrailerAsync(hlsUrls, maxHeight, maxSeconds, path, ct));
                }

                if (micro == null && settings.ProvideMicrotrailers && movie.MicrotrailerUrl != null)
                {
                    micro = new Uri(movie.MicrotrailerUrl);
                    providedMicro = true;
                }
            }
            else if (settings.YoutubeFallback && hasFfmpeg && plugin.ResolveYtDlpPath() != null)
            {
                // No Steam trailer (non-Steam game, or a Steam app without movies): look on YouTube.
                // The search runs in the proxy's background job, never on this UI thread.
                var query = YoutubeTrailerDownloader.BuildQuery(game.Name, game.ReleaseDate?.Year);
                var gameKey = game.Id.ToString();
                var youtube = plugin.Youtube;
                if (!youtube.IsKnownMissing(gameKey, query))
                {
                    trailer = plugin.Server.Prepare($"youtube|{gameKey}|{query}|{maxHeight}|{maxSeconds}", $"YouTube search \"{query}\"",
                        (path, ct) => youtube.DownloadAsync(gameKey, query, maxHeight, maxSeconds, path, ct));
                }
            }

            if (trailer == null && !providedMicro)
            {
                return;
            }

            trailerVideoPathField.SetValue(control, trailer);
            microVideoPathField.SetValue(control, micro);
            multipleSourcesAvailableField.SetValue(control, trailer != null && micro != null);

            SetProperty(emlSettings, "IsAnyVideoAvailable", true);
            if (trailer != null)
            {
                SetProperty(emlSettings, "IsTrailerAvailable", true);
            }
            // IsMicrotrailerAvailable is deliberately left alone for streamed microtrailers: themes such as
            // Solaris read it as "VideoMicrotrailer.mp4 exists locally", hide EML's player and try to play that
            // file themselves. The streamed one is still reachable through EML's switch-source button.

            // Same source selection EML performs at the end of UpdateGameVideoSources.
            var useMicro = (bool)useMicrovideosSourceField.GetValue(control);
            var fallback = GetProperty(emlSettings, "FallbackVideoSource") as bool? ?? true;
            Uri chosen = null;
            string chosenType = null;
            if (useMicro)
            {
                if (micro != null) { chosen = micro; chosenType = "Microtrailer"; }
                else if (trailer != null && fallback) { chosen = trailer; chosenType = "Trailer"; }
            }
            else
            {
                if (trailer != null) { chosen = trailer; chosenType = "Trailer"; }
                else if (micro != null && fallback) { chosen = micro; chosenType = "Microtrailer"; }
            }

            if (chosen != null && !Equals(videoSourceProperty.GetValue(control), chosen))
            {
                videoSourceProperty.SetValue(control, chosen);
                activeVideoTypeField.SetValue(control, Enum.Parse(activeVideoTypeField.FieldType, chosenType));
            }
        }

        private static object GetProperty(object target, string name)
        {
            return target?.GetType().GetProperty(name, instanceFlags)?.GetValue(target);
        }

        private static void SetProperty(object target, string name, object value)
        {
            var property = target?.GetType().GetProperty(name, instanceFlags);
            if (property?.CanWrite == true)
            {
                property.SetValue(target, value);
            }
        }
    }
}

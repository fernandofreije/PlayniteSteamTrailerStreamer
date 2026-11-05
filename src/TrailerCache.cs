using Playnite.SDK;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SteamTrailerStreamer
{
    /// <summary>
    /// Temporary folder holding remuxed trailers. Trimmed to a size limit (least recently used first)
    /// and wiped on startup and exit.
    /// </summary>
    public class TrailerCache
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly object trimLock = new object();
        private long limitBytes;

        public string Directory { get; }

        public TrailerCache(string directory, int limitMb)
        {
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            SetLimit(limitMb);
        }

        public void SetLimit(int limitMb)
        {
            limitBytes = Math.Max(50, limitMb) * 1024L * 1024L;
        }

        public static string MakeId(string text)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public string GetPath(string id)
        {
            return Path.Combine(Directory, id + ".mp4");
        }

        public void MarkUsed(string path)
        {
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Trim(string keepPath)
        {
            lock (trimLock)
            {
                var files = new DirectoryInfo(Directory).GetFiles("*.mp4")
                    .OrderByDescending(f => f.LastAccessTimeUtc)
                    .ToList();
                var total = files.Sum(f => f.Length);
                foreach (var file in files.AsEnumerable().Reverse())
                {
                    if (total <= limitBytes)
                    {
                        break;
                    }

                    if (string.Equals(file.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryDelete(file.FullName))
                    {
                        total -= file.Length;
                    }
                }
            }
        }

        public void Clear()
        {
            DeleteFiles("*");
        }

        /// <summary>Removes leftovers of conversions that were interrupted (e.g. by closing Playnite).</summary>
        public void DeletePartialFiles()
        {
            DeleteFiles("*.part");
        }

        private void DeleteFiles(string pattern)
        {
            lock (trimLock)
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return;
                }

                foreach (var file in System.IO.Directory.GetFiles(Directory, pattern))
                {
                    TryDelete(file);
                }
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Likely still being served to the player; it will be removed next time.
                logger.Debug($"Could not delete cached trailer {path}: {e.Message}");
                return false;
            }
        }
    }
}

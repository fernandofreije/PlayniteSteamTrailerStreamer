using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamTrailerStreamer
{
    /// <summary>
    /// Minimal HTTP/1.1 server on 127.0.0.1. MediaElement requests /trailer?id={id} for a trailer
    /// registered with <see cref="Prepare"/>; it is produced once into the temporary cache and then served
    /// with Range support so seeking and looping work. Only registered ids are served, so the port can't be
    /// used to fetch arbitrary URLs.
    /// A raw TcpListener avoids HttpListener's URL ACL / admin requirements.
    /// </summary>
    public class TrailerProxyServer
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Regex rangeRegex = new Regex(@"bytes=(\d*)-(\d*)", RegexOptions.Compiled);
        private const int bufferSize = 81920;

        private readonly TrailerCache cache;
        private readonly ConcurrentDictionary<string, TrailerRequest> requests = new ConcurrentDictionary<string, TrailerRequest>();
        private readonly ConcurrentDictionary<string, TrailerJob> jobs = new ConcurrentDictionary<string, TrailerJob>();
        // Fast scrolling through games must not spawn a pile of ffmpeg processes.
        private readonly SemaphoreSlim remuxSlots = new SemaphoreSlim(2);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private TcpListener listener;

        public int Port { get; private set; }

        public TrailerProxyServer(TrailerCache cache)
        {
            this.cache = cache;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(AcceptLoopAsync);
            logger.Info($"Trailer proxy listening on 127.0.0.1:{Port}");
        }

        public void Stop()
        {
            shutdown.Cancel();
            try
            {
                listener?.Stop();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Registers a game's trailer and starts converting it immediately, so it is often ready by the time
        /// the player asks for it. Conversions for games the user already moved away from are cancelled.
        /// <paramref name="produce"/> writes the playable MP4 to the path it is given; it runs in the background
        /// job (never on the caller's thread), so it may be slow, and must honour the cancellation token.
        /// </summary>
        /// <param name="key">Identifies the trailer and its quality limits; equal keys share one cached file.</param>
        /// <param name="description">Shown in the log when the trailer fails.</param>
        public Uri Prepare(string key, string description, Func<string, CancellationToken, Task> produce)
        {
            var id = TrailerCache.MakeId(key);
            var request = new TrailerRequest
            {
                Description = description,
                Produce = produce,
                Path = cache.GetPath(id)
            };
            requests[id] = request;

            CancelIdleJobs(except: id);
            if (File.Exists(request.Path))
            {
                cache.MarkUsed(request.Path);
            }
            else
            {
                GetOrStartJob(id, request);
            }

            return new Uri($"http://127.0.0.1:{Port}/trailer?id={id}");
        }

        private void CancelIdleJobs(string except)
        {
            foreach (var pair in jobs)
            {
                if (pair.Key != except && pair.Value.Waiters == 0 && !pair.Value.Task.IsCompleted)
                {
                    // Called from EML's UI-thread code; stopping ffmpeg can take a moment, so do it off-thread.
                    var job = pair.Value;
                    Task.Run(() =>
                    {
                        try
                        {
                            job.Cancellation.Cancel();
                        }
                        catch (ObjectDisposedException) { }
                    });
                }
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.Error(e, "Trailer proxy accept failed");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (request == null)
                    {
                        return;
                    }

                    if (request.Method != "GET" && request.Method != "HEAD")
                    {
                        await WriteStatusAsync(stream, "405 Method Not Allowed").ConfigureAwait(false);
                        return;
                    }

                    var query = ParseQuery(request.Target, out var path);
                    if (path != "/trailer" || !query.TryGetValue("id", out var id) || !requests.TryGetValue(id, out var trailerRequest))
                    {
                        await WriteStatusAsync(stream, "404 Not Found").ConfigureAwait(false);
                        return;
                    }

                    var file = trailerRequest.Path;
                    if (File.Exists(file))
                    {
                        cache.MarkUsed(file);
                    }
                    else
                    {
                        var job = GetOrStartJob(id, trailerRequest);
                        Interlocked.Increment(ref job.Waiters);
                        try
                        {
                            file = await job.Task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await WriteStatusAsync(stream, "503 Service Unavailable").ConfigureAwait(false);
                            return;
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, $"Failed to prepare trailer {trailerRequest.Description}");
                            await WriteStatusAsync(stream, "502 Bad Gateway").ConfigureAwait(false);
                            return;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref job.Waiters);
                        }
                    }

                    await ServeFileAsync(stream, file, request).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Player closed the connection (seek, game change, ...). Nothing to do.
                }
                catch (Exception e)
                {
                    logger.Error(e, "Trailer proxy request failed");
                }
            }
        }

        private TrailerJob GetOrStartJob(string id, TrailerRequest request)
        {
            while (true)
            {
                if (jobs.TryGetValue(id, out var existing) && !existing.Cancellation.IsCancellationRequested)
                {
                    return existing;
                }

                var job = new TrailerJob { Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token) };
                var registered = existing != null ? jobs.TryUpdate(id, job, existing) : jobs.TryAdd(id, job);
                if (!registered)
                {
                    job.Cancellation.Dispose();
                    continue;
                }

                job.Task = Task.Run(() => RunJobAsync(request, job.Cancellation.Token));
                // Forget finished jobs so failures can be retried and successes are served from disk.
                job.Task.ContinueWith(_ =>
                    ((ICollection<KeyValuePair<string, TrailerJob>>)jobs).Remove(new KeyValuePair<string, TrailerJob>(id, job)),
                    TaskScheduler.Default);
                return job;
            }
        }

        private async Task<string> RunJobAsync(TrailerRequest request, CancellationToken cancellationToken)
        {
            await remuxSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(request.Path))
                {
                    await request.Produce(request.Path, cancellationToken).ConfigureAwait(false);
                }
                cache.MarkUsed(request.Path);
                cache.Trim(request.Path);
                return request.Path;
            }
            finally
            {
                remuxSlots.Release();
            }
        }

        private static async Task ServeFileAsync(NetworkStream stream, string file, HttpRequest request)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, true))
            {
                var length = fs.Length;
                long start = 0;
                long end = length - 1;
                var partial = false;

                if (request.Headers.TryGetValue("range", out var rangeHeader))
                {
                    var match = rangeRegex.Match(rangeHeader);
                    if (match.Success)
                    {
                        var startText = match.Groups[1].Value;
                        var endText = match.Groups[2].Value;
                        if (startText.Length == 0 && endText.Length > 0)
                        {
                            start = Math.Max(0, length - long.Parse(endText));
                        }
                        else
                        {
                            start = startText.Length > 0 ? long.Parse(startText) : 0;
                            end = endText.Length > 0 ? Math.Min(long.Parse(endText), length - 1) : length - 1;
                        }

                        if (start >= length || start > end)
                        {
                            await WriteRawAsync(stream,
                                $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                            return;
                        }
                        partial = true;
                    }
                }

                var count = end - start + 1;
                var header = new StringBuilder();
                header.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                header.Append("Content-Type: video/mp4\r\n");
                header.Append("Accept-Ranges: bytes\r\n");
                header.Append($"Content-Length: {count}\r\n");
                if (partial)
                {
                    header.Append($"Content-Range: bytes {start}-{end}/{length}\r\n");
                }
                header.Append("Cache-Control: no-store\r\n");
                header.Append("Connection: close\r\n\r\n");
                await WriteRawAsync(stream, header.ToString()).ConfigureAwait(false);

                if (request.Method == "HEAD")
                {
                    return;
                }

                fs.Position = start;
                var buffer = new byte[bufferSize];
                var remaining = count;
                while (remaining > 0)
                {
                    var read = await fs.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }
                    await stream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    remaining -= read;
                }
            }
        }

        private static Dictionary<string, string> ParseQuery(string target, out string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var questionMark = target.IndexOf('?');
            path = questionMark >= 0 ? target.Substring(0, questionMark) : target;
            if (questionMark < 0)
            {
                return result;
            }

            foreach (var pair in target.Substring(questionMark + 1).Split('&'))
            {
                var equals = pair.IndexOf('=');
                if (equals > 0)
                {
                    result[Uri.UnescapeDataString(pair.Substring(0, equals))] = Uri.UnescapeDataString(pair.Substring(equals + 1));
                }
            }
            return result;
        }

        private class TrailerRequest
        {
            public string Description;
            public Func<string, CancellationToken, Task> Produce;
            public string Path;
        }

        private class TrailerJob
        {
            public Task<string> Task;
            public CancellationTokenSource Cancellation;
            // HTTP requests currently waiting on this job; jobs nobody waits for may be cancelled.
            public int Waiters;
        }

        private class HttpRequest
        {
            public string Method;
            public string Target;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream)
        {
            // Read byte-wise up to the blank line so no body bytes are consumed (GET/HEAD have none anyway).
            var headerBytes = new List<byte>(1024);
            var single = new byte[1];
            while (headerBytes.Count < 16 * 1024)
            {
                var read = await stream.ReadAsync(single, 0, 1).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                headerBytes.Add(single[0]);
                var n = headerBytes.Count;
                if (n >= 4 && headerBytes[n - 4] == '\r' && headerBytes[n - 3] == '\n' && headerBytes[n - 2] == '\r' && headerBytes[n - 1] == '\n')
                {
                    break;
                }
            }

            var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2)
            {
                return null;
            }

            var request = new HttpRequest { Method = requestLine[0].ToUpperInvariant(), Target = requestLine[1] };
            for (int i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }
            return request;
        }

        private static Task WriteStatusAsync(NetworkStream stream, string status)
        {
            return WriteRawAsync(stream, $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }

        private static Task WriteRawAsync(NetworkStream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}

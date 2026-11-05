using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamTrailerStreamer
{
    /// <summary>
    /// Runs a hidden console tool (ffmpeg, yt-dlp) and returns its stdout. The process is killed on
    /// cancellation or timeout, and a non-zero exit code becomes an exception carrying stderr.
    /// </summary>
    internal static class ProcessRunner
    {
        public static Task<string> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<string>();
            var output = new StringBuilder();
            var errors = new StringBuilder();
            var toolName = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            // This runs on a thread-pool callback: an exception escaping it would crash Playnite, so it
            // must never throw. It is also the only place the process is disposed, because it can fire
            // after a cancellation has already completed the task.
            process.Exited += (s, e) =>
            {
                try
                {
                    // Make sure the async readers have flushed before we read the exit code.
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        lock (output) completion.TrySetResult(output.ToString());
                    }
                    else
                    {
                        lock (errors) completion.TrySetException(new Exception($"{toolName} exited with code {process.ExitCode}: {errors}"));
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    process.Dispose();
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var timeoutSource = new CancellationTokenSource(timeout);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var registration = linked.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        KillTree(process);
                        // Wait so output files are released before the caller deletes them.
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception) { }
                completion.TrySetCanceled();
            });

            return completion.Task.ContinueWith(t =>
            {
                registration.Dispose();
                linked.Dispose();
                timeoutSource.Dispose();
                return t;
            }, TaskScheduler.Default).Unwrap();
        }

        /// <summary>
        /// Kills the process and its children (yt-dlp runs ffmpeg to merge). .NET Framework's Process.Kill
        /// only kills the process itself, so use taskkill /T and fall back to a plain kill.
        /// </summary>
        private static void KillTree(Process process)
        {
            try
            {
                using (var taskkill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    taskkill?.WaitForExit(5000);
                }
            }
            catch (Exception) { }

            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }
}

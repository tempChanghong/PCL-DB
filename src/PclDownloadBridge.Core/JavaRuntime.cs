using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;

namespace PclDownloadBridge.Core
{

    [Microsoft.VisualBasic.CompilerServices.StandardModule]
    public sealed class JavaRuntime
    {
        private JavaRuntime() { }

        public static string Find(string explicitPath, int minimum)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string full = Path.GetFullPath(explicitPath);
                if (!File.Exists(full))
                    throw new FileNotFoundException("找不到所选 Java：" + full);
                if (Major(full) < minimum)
                    throw new InvalidOperationException("安装器需要 Java " + minimum + " 或更高版本。");
                return full;
            }
            var candidates = new List<string>();
            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
                candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
            foreach (var directory in Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    candidates.Add(Path.Combine(directory.Trim('"'), "java.exe"));
            }
            foreach (var @base in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks") })
            {
                if (Directory.Exists(@base))
                {
                    foreach (var directory in Directory.EnumerateDirectories(@base))
                        candidates.Add(Path.Combine(directory, "bin", "java.exe"));
                }
            }
            var available = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => File.Exists(p)).Select(p => new { Path = p, Version = Major(p) }).Where(j => j.Version >= minimum).OrderBy(j => j.Version).ToList();
            if (available.Count == 0)
                throw new InvalidOperationException("未发现可用的 Java " + minimum + " 或更高版本。请在下载设置中选择 java.exe。");
            return available[0].Path;
        }
        private static int Major(string java)
        {
            try
            {
                using (var process = new Process() { StartInfo = new ProcessStartInfo(java, "-version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } })
                {
                    var clock = Stopwatch.StartNew();
                    process.Start();
                    using (var output = process.StandardOutput)
                    using (var errors = process.StandardError)
                    {
                        // Both pipes must drain before waiting; JVM option notices can fill either.
                        var stdout = ReadMajorAsync(output);
                        var stderr = ReadMajorAsync(errors);
                        if (!process.WaitForExit((int)Math.Max(0L, 5000L - clock.ElapsedMilliseconds)))
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                            return 0;
                        }
                        if (!Task.WhenAll(stdout, stderr).Wait((int)Math.Max(0L, 5000L - clock.ElapsedMilliseconds)))
                            return 0;
                        if (process.ExitCode != 0)
                            return 0;
                        return stderr.Result > 0 ? stderr.Result : stdout.Result;
                    }
                }
            }
            catch
            {
                return 0;
            }
        }
        private async static Task<int> ReadMajorAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            var line = new StringBuilder();
            var overflow = default(bool);
            var version = default(int);
            try
            {
                do
                {
                    int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    for (int index = 0, loopTo = count - 1; index <= loopTo; index++)
                    {
                        if (buffer[index] == ControlChars.Cr || buffer[index] == ControlChars.Lf)
                        {
                            if (version == 0 && !overflow)
                                version = ParseMajor(line.ToString());
                            line.Clear();
                            overflow = false;
                        }
                        else if (version == 0 && !overflow)
                        {
                            if (line.Length == 4096)
                            {
                                line.Clear();
                                overflow = true;
                            }
                            else
                            {
                                line.Append(buffer[index]);
                            }
                        }
                    }
                }
                while (true);
                if (version == 0 && !overflow)
                    version = ParseMajor(line.ToString());
                return version;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }
        private static int ParseMajor(string line)
        {
            // PCL also recognizes the unquoted OpenJDK banner. Ignore option echoes.
            var match = Regex.Match(line, @"\A\s*(?:(?:java|openjdk)(?: full)? version ""(?<version>[0-9][0-9A-Za-z._+\-]{0,127})""|openjdk (?<version>[0-9][0-9A-Za-z._+\-]{0,127})(?=\s|\z))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return 0;
            string value = match.Groups["version"].Value;
            if (value.StartsWith("1.", StringComparison.Ordinal))
                value = value.Substring(2);
            int result;
            return int.TryParse(value.Split('.', '_', '+', '-')[0], out result) && result > 0 ? result : 0;
        }
        public async static Task RunAsync(string java, IEnumerable<string> arguments, string work, Action<string> report, CancellationToken token)
        {
            var info = new ProcessStartInfo() { FileName = java, Arguments = string.Join(" ", arguments.Select(Quote)), WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var process = new Process() { StartInfo = info, EnableRaisingEvents = true })
            {
                process.Exited += (sender, e) => completion.TrySetResult(true);
                var lines = new Queue<string>();
                var outputEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var errorEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool accepting = true;
                // A full/removed log destination must not crash a stream callback.
                Action<string> consume = line => { lock (lines) { if (!accepting) return; lines.Enqueue(line); if (lines.Count > 12) lines.Dequeue(); try { report?.Invoke(line); } catch (IOException) { } } };
                DataReceivedEventHandler output = (sender, e) => { if (e.Data is null) outputEnd.TrySetResult(true); else consume(e.Data); };
                DataReceivedEventHandler errors = (sender, e) => { if (e.Data is null) errorEnd.TrySetResult(true); else consume(e.Data); };
                process.OutputDataReceived += output;
                process.ErrorDataReceived += errors;
                try
                {
                    token.ThrowIfCancellationRequested();
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    using (var registration = token.Register(() =>
                        {
                            cancelled.TrySetResult(true);
                            try
                            {
                                if (!process.HasExited)
                                    process.Kill();
                            }
                            catch (InvalidOperationException)
                            {
                            }
                        }))
                    {
                        await completion.Task.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        // A descendant can retain these pipes after the owned process exits.
                        // Bound EOF draining, and keep cancellation responsive during it.
                        var streams = Task.WhenAll(outputEnd.Task, errorEnd.Task);
                        var finished = await Task.WhenAny(streams, Task.Delay(5000), cancelled.Task).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        string tail;
                        lock (lines)
                            tail = string.Join(Constants.vbCrLf, lines);
                        if (!ReferenceEquals(finished, streams))
                            throw new IOException("Java 安装处理器已退出 (" + process.ExitCode + ")，但输出流未在 5 秒内结束，请重试。" + Constants.vbCrLf + tail);
                        if (process.ExitCode != 0)
                            throw new IOException("Java 安装处理器失败 (" + process.ExitCode + ")：" + tail);
                    }
                }
                finally
                {
                    lock (lines)
                        accepting = false;
                    process.OutputDataReceived -= output;
                    process.ErrorDataReceived -= errors;
                    try
                    {
                        process.CancelOutputRead();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    try
                    {
                        process.CancelErrorRead();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }
        public static string Quote(string value)
        {
            if (value.Contains(Constants.vbCr) || value.Contains(Constants.vbLf))
                throw new ArgumentException("进程参数含换行。");
            // Windows CommandLineToArgvW escaping, including trailing backslashes.
            return "\"" + Regex.Replace(value, @"(\\*)""", m => new string('\\', m.Groups[1].Length * 2 + 1) + "\"") + (value.EndsWith(@"\", StringComparison.Ordinal) ? new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count()) : "") + "\"";
        }
    }
}

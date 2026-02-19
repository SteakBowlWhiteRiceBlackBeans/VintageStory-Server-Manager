using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Drewski_s_Server_Manager
{
    public static class CrashLogger
    {
        private static string? _logDir;
        private static bool _startLineWritten;

        private const long MaxSizeBytes = 3 * 1024 * 1024; // 3 MB

        private static string LogFile =>
            Path.Combine(_logDir!, "DServerManager.txt");

        public static void InitGlobalExceptionLogging()
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                Application.ThreadException += (s, e) =>
                {
                    Log("UI Thread Crash", e.Exception);
                };

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    if (e.ExceptionObject is Exception ex)
                        Log("Non-UI Crash", ex);
                    else
                        Log("Non-UI Crash (Unknown ExceptionObject)");
                };

                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    try
                    {
                        Log("Unobserved Task Exception", e.Exception);
                    }
                    finally
                    {
                        e.SetObserved();
                    }
                };
            }
            catch
            {
                // never allow logger init to crash the app
            }
        }

        public static void SetServerDataPath(string? serverDataPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(serverDataPath))
                {
                    _logDir = null;
                    return;
                }

                var resolved = ResolvePath(serverDataPath);
                _logDir = Path.Combine(resolved, "logs", "DSM");
                Directory.CreateDirectory(_logDir);

                WriteStartLineOnce();
            }
            catch
            {
                // never throw from logger setup
            }
        }

        private static void WriteStartLineOnce()
        {
            try
            {
                if (_startLineWritten) return;
                if (string.IsNullOrWhiteSpace(_logDir)) return;

                _startLineWritten = true;

                var fullPath = LogFile;

                // Console line (will only show if you have a console / are running under one)
                try
                {
                    Console.WriteLine($"Log file started at {fullPath}");
                }
                catch
                {
                    // ignore if no console available
                }

                // Also write it into the file so you can always see it
                Log($"Log file started at {fullPath}");
            }
            catch
            {
                // ignore
            }
        }

        private static string ResolvePath(string path)
        {
            var normalized = (path ?? string.Empty).Replace('/', '\\');
            return Environment.ExpandEnvironmentVariables(normalized);
        }

        public static void Log(string message, Exception? ex = null)
        {
            try
            {
                // If the app hasn't set the server data path yet, do nothing.
                // This prevents logs writing to random fallback locations.
                if (string.IsNullOrWhiteSpace(_logDir))
                    return;

                Directory.CreateDirectory(_logDir);
                RotateIfNeeded();

                var sb = new StringBuilder();
                sb.AppendLine("--------------------------------------------------");
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                try
                {
                    var fileVer = FileVersionInfo.GetVersionInfo(Application.ExecutablePath).FileVersion ?? "Unknown";
                    sb.AppendLine($"App Version: {fileVer}");
                }
                catch { }

                sb.AppendLine(message);

                if (ex != null)
                {
                    sb.AppendLine("Exception:");
                    sb.AppendLine(ex.ToString());
                }

                sb.AppendLine();

                File.AppendAllText(LogFile, sb.ToString());
            }
            catch
            {
                // logger must never throw
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_logDir))
                    return;

                if (!File.Exists(LogFile))
                    return;

                var fi = new FileInfo(LogFile);
                if (fi.Length < MaxSizeBytes)
                    return;

                // Keep a single log file. Trim the oldest content and keep the most
                // recent MaxSizeBytes bytes to enforce the size limit.
                var temp = LogFile + ".tmp";
                try
                {
                    using (var src = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long keep = MaxSizeBytes;
                        if (src.Length <= keep)
                        {
                            return;
                        }

                        src.Seek(-keep, SeekOrigin.End);

                        var buffer = new byte[keep];
                        int read = 0;
                        while (read < buffer.Length)
                        {
                            int r = src.Read(buffer, read, buffer.Length - read);
                            if (r <= 0) break;
                            read += r;
                        }

                        File.WriteAllBytes(temp, buffer.AsSpan(0, read).ToArray());
                    }

                    File.Delete(LogFile);
                    File.Move(temp, LogFile);
                }
                catch
                {
                    try { File.WriteAllText(LogFile, string.Empty); } catch { }
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}

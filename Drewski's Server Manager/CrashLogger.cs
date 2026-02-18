using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Drewski_s_Server_Manager
{
    public static class CrashLogger
    {

        private static string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VintagestoryData",
            "Logs");

        private static string LogFile => Path.Combine(_logDir, "DServerManager.txt");


        private const long MaxSizeBytes = 5 * 1024 * 1024;

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


                Log("CrashLogger initialized");
            }
            catch
            {
                // never allow logger to crash the app
            }
        }

        // Call this after settings are loaded to move logs into the server data folder.
        public static void SetServerDataPath(string? serverDataPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(serverDataPath))
                {
                    // reset to default
                    _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData", "Logs");
                    return;
                }

                var resolved = ResolvePath(serverDataPath);
                // place logs under the server data folder in a "Logs" subfolder
                _logDir = Path.Combine(resolved, "Logs");
            }
            catch
            {
                // never throw from logger setup
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

            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(LogFile)) return;

                var fi = new FileInfo(LogFile);
                if (fi.Length < MaxSizeBytes) return;

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

                    // Replace original with trimmed file
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

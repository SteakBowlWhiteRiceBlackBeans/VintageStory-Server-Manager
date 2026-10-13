using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VintageStory_Server_Manager
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings;
        private DispatcherTimer _statsTimer;
        private DateTime _lastCpuTime = DateTime.UtcNow;
        private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
        private DispatcherTimer? _autoSaveTimer;
        private DispatcherTimer? _autoBackupTimer;
        private DispatcherTimer? _autoRestartTimer;
        private DateTime _lastAutoRestartDate = DateTime.MinValue;
        private HashSet<int> _sentRestartWarnings = new();


        public MainWindow()
        {
            InitializeComponent();
            _settings = AppConfig.LoadOrCreate();
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += UpdateStats;
            _statsTimer.Start();
        }

        private void UpdateStats(object? sender, EventArgs e)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                CpuText.Text = "CPU: --";
                RamText.Text = "RAM: --";
                ServerIP.Text = "Server IP: --";
                ServerPort.Text = "Server Port: --";
                return;
            }
            try
            {
                _serverProcess.Refresh();
                long ramMb = _serverProcess.WorkingSet64 / (1024 * 1024);
                RamText.Text = $"RAM: {ramMb} MB";
                var now = DateTime.UtcNow;
                var cpuTime = _serverProcess.TotalProcessorTime;
                var elapsed = now - _lastCpuTime;
                if (elapsed.TotalMilliseconds > 0 && _lastTotalProcessorTime != TimeSpan.Zero)
                {
                    double cpu = (cpuTime - _lastTotalProcessorTime).TotalMilliseconds
                                 / elapsed.TotalMilliseconds
                                 / Environment.ProcessorCount * 100;
                    CpuText.Text = $"CPU: {cpu:0.0}%";
                }
                _lastCpuTime = now;
                _lastTotalProcessorTime = cpuTime;
            }
            catch
            {
                CpuText.Text = "CPU: --";
                RamText.Text = "RAM: --";
            }
        }

        private const int MaxConsoleLines = 3000;

        private void AppendConsole(string text)
        {
            ConsoleOutput.AppendText(text);
            ConsoleOutput.ScrollToEnd();
            int lineCount = ConsoleOutput.LineCount;
            if (lineCount > MaxConsoleLines)
            {
                int linesToRemove = lineCount - MaxConsoleLines;
                int charIndex = ConsoleOutput.GetCharacterIndexFromLineIndex(linesToRemove);
                ConsoleOutput.Text = ConsoleOutput.Text.Substring(charIndex);
                ConsoleOutput.ScrollToEnd();
            }
        }

        private void EnforceBackupLimits()
        {
            _settings = AppConfig.LoadOrCreate();
            if (_settings.MaxBackupFiles <= 0 && _settings.MaxBackupFolderSizeGB <= 0)
                return;

            string dataPath = Environment.ExpandEnvironmentVariables(_settings.DataFolder);
            string backupDir = System.IO.Path.Combine(dataPath, "Backups");
            if (!Directory.Exists(backupDir))
                return;

            var files = new DirectoryInfo(backupDir)
                .GetFiles()
                .OrderBy(f => f.CreationTime)
                .ToList();

            if (_settings.MaxBackupFiles > 0)
            {
                while (files.Count >= _settings.MaxBackupFiles)
                {
                    try
                    {
                        string name = files[0].Name;
                        files[0].Delete();
                        AppendConsole($"Deleted old backup (file limit): {name}" + Environment.NewLine);
                        files.RemoveAt(0);
                    }
                    catch (Exception ex)
                    {
                        AppendConsole($"Failed to delete backup: {ex.Message}" + Environment.NewLine);
                        break;
                    }
                }
            }

            if (_settings.MaxBackupFolderSizeGB > 0)
            {
                long maxBytes = (long)_settings.MaxBackupFolderSizeGB * 1024 * 1024 * 1024;
                long total = files.Sum(f => f.Length);

                while (files.Count > 0 && total > maxBytes)
                {
                    try
                    {
                        string name = files[0].Name;
                        long size = files[0].Length;
                        files[0].Delete();
                        AppendConsole($"Deleted old backup (size limit): {name}" + Environment.NewLine);
                        total -= size;
                        files.RemoveAt(0);
                    }
                    catch (Exception ex)
                    {
                        AppendConsole($"Failed to delete backup: {ex.Message}" + Environment.NewLine);
                        break;
                    }
                }
            }
        }

        private void StartAutomationTimers()
        {
            StopAutomationTimers();
            _settings = AppConfig.LoadOrCreate();
            if (_settings.AutoSaveEnabled && _settings.AutoSaveMinutes > 0)
            {
                _autoSaveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(_settings.AutoSaveMinutes)
                };
                _autoSaveTimer.Tick += (s, e) =>
                {
                    if (_serverProcess != null && !_serverProcess.HasExited)
                    {
                        _serverProcess.StandardInput.WriteLine("/autosavenow");
                        AppendConsole("> /autosavenow (auto)" + Environment.NewLine);
                    }
                };
                _autoSaveTimer.Start();
            }
            if (_settings.AutoBackupEnabled && _settings.AutoBackupMinutes > 0)
            {
                _autoBackupTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(_settings.AutoBackupMinutes)
                };
                _autoBackupTimer.Tick += (s, e) =>
                {
                    if (_serverProcess != null && !_serverProcess.HasExited)
                    {
                        EnforceBackupLimits();
                        string stamp = DateTime.Now.ToString("MM_dd_yy_h:mmtt").ToLower();
                        string cmd = $"/genbackup VS_Backup_{stamp}";
                        _serverProcess.StandardInput.WriteLine(cmd);
                        AppendConsole("> " + cmd + " (auto)" + Environment.NewLine);
                    }
                };
                _autoBackupTimer.Start();
            }
        }

        private void StopAutomationTimers()
        {
            _autoSaveTimer?.Stop();
            _autoSaveTimer = null;
            _autoBackupTimer?.Stop();
            _autoBackupTimer = null;
            _autoRestartTimer?.Stop();
            _autoRestartTimer = null;
        }

        private void ModPageButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://mods.vintagestory.at/show/mod/41122",
                UseShellExecute = true
            });
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow();
            settings.Owner = this;
            settings.ShowDialog();

            // Added this bit to update auto stuff after its already started
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                StartAutomationTimers();
                StartAutoRestartTimer();
                AppendConsole("Automation settings reloaded." + Environment.NewLine);
            }
        }


        private async void UpdateServerAddress()
        {
            // Port from serverconfig.json
            _settings = AppConfig.LoadOrCreate();
            string dataPath = Environment.ExpandEnvironmentVariables(_settings.DataFolder);
            string configFile = System.IO.Path.Combine(dataPath, "serverconfig.json");

            int port = 42420;
            if (File.Exists(configFile))
            {
                try
                {
                    string json = File.ReadAllText(configFile);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Port", out var portProp))
                        port = portProp.GetInt32();
                }
                catch { }
            }

            // Public IP
            string ip = "--";
            try
            {
                using var client = new System.Net.Http.HttpClient();
                ip = (await client.GetStringAsync("https://api.ipify.org")).Trim();
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                ServerIP.Text = $"Server IP: {ip}";
                ServerPort.Text = $"Server Port: {port}";
            });
        }


        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CommandInput.Text)) return;
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                AppendConsole("Server is not running." + Environment.NewLine);
                return;
            }
            string cmd = CommandInput.Text.Trim();
            _serverProcess.StandardInput.WriteLine(cmd);
            AppendConsole("> " + cmd + Environment.NewLine);
            CommandInput.Clear();
        }

        private void CommandInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SendButton_Click(sender, e);
        }

        private Process? _serverProcess;

        private void StartServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
                return;
            _settings = AppConfig.LoadOrCreate();
            string exePath = Environment.ExpandEnvironmentVariables(_settings.ServerExecutable);
            string dataPath = Environment.ExpandEnvironmentVariables(_settings.DataFolder);
            string arguments = _settings.LaunchArguments ?? "";
            if (!string.IsNullOrWhiteSpace(dataPath))
            {
                arguments = $"--dataPath=\"{dataPath}\" {arguments}".Trim();
            }
            if (!File.Exists(exePath))
            {
                AppendConsole("Server executable not found: " + exePath + Environment.NewLine);
                return;
            }
            AppendConsole("Launching with arguments: " + arguments + Environment.NewLine);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath)
            };
            _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _serverProcess.OutputDataReceived += Server_OutputDataReceived;
            _serverProcess.ErrorDataReceived += Server_OutputDataReceived;
            _serverProcess.Exited += Server_Exited;
            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();
            AppendConsole("Server started." + Environment.NewLine);
            StartAutomationTimers();
            StartAutoRestartTimer();
            UpdateServerAddress();
        }

        private void StopServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                AppendConsole("Server is not running." + Environment.NewLine);
                return;
            }
            StopAutomationTimers();
            
            _serverProcess.StandardInput.WriteLine("/stop");
            AppendConsole("> /stop" + Environment.NewLine);
        }

        private void Server_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            Dispatcher.Invoke(() => AppendConsole(e.Data + Environment.NewLine));
        }

        private void Server_Exited(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StopAutomationTimers();
                AppendConsole("Server process exited." + Environment.NewLine);
            });
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                AppendConsole("Server is not running." + Environment.NewLine);
                return;
            }
            _serverProcess.StandardInput.WriteLine("/autosavenow");
            AppendConsole("> /autosavenow" + Environment.NewLine);
        }

        private void BackupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                AppendConsole("Server is not running." + Environment.NewLine);
                return;
            }

            EnforceBackupLimits();

            string stamp = DateTime.Now.ToString("MM_dd_yy_h:mmtt").ToLower();
            string filename = $"VS_Backup_{stamp}";
            string cmd = $"/genbackup {filename}";
            _serverProcess.StandardInput.WriteLine(cmd);
            AppendConsole("> " + cmd + Environment.NewLine);
        }

        private void KillServerButton_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Are you sure you want to kill the server? Unsaved progress will be lost.",
                "Kill Server",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
            bool killed = false;
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                try
                {
                    _serverProcess.Kill(true);
                    killed = true;
                }
                catch { }
            }
            foreach (var proc in Process.GetProcessesByName("VintagestoryServer"))
            {
                try
                {
                    proc.Kill(true);
                    killed = true;
                }
                catch { }
            }
            if (killed)
                AppendConsole("Server killed." + Environment.NewLine);
            else
                AppendConsole("No running server process found." + Environment.NewLine);
            StopAutomationTimers();
            _serverProcess = null;
            ServerIP.Text = "Server IP: --";
            ServerPort.Text = "Server Port: --";
        }

        private async void RestartServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                StartServerButton_Click(sender, e);
                return;
            }
            StopAutomationTimers();
            AppendConsole("> /stop" + Environment.NewLine);
            _serverProcess.StandardInput.WriteLine("/stop");
            bool exited = await Task.Run(() => _serverProcess.WaitForExit(30000));
            if (!exited)
            {
                AppendConsole("Server did not stop in time, killing..." + Environment.NewLine);
                try { _serverProcess.Kill(true); } catch { }
                await Task.Delay(1000);
            }
            _serverProcess = null;
            StartServerButton_Click(sender, e);
        }

        private void BackupFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string backupPath = Environment.ExpandEnvironmentVariables(@"%appdata%\VintagestoryData\Backups");
            if (!Directory.Exists(backupPath))
            {
                AppendConsole("Backup folder not found: " + backupPath + Environment.NewLine);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = backupPath,
                UseShellExecute = true
            });
        }

  

        private void StartAutoRestartTimer()
        {
            _autoRestartTimer?.Stop();
            _autoRestartTimer = null;

            _settings = AppConfig.LoadOrCreate();
            if (!_settings.AutoRestartEnabled)
                return;

            _autoRestartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _autoRestartTimer.Tick += (s, e) =>
            {
                if (_serverProcess == null || _serverProcess.HasExited)
                    return;

                var now = DateTime.Now;
                int targetHour = _settings.RestartHour;
                if (_settings.RestartAmPm.Equals("PM", StringComparison.OrdinalIgnoreCase) && targetHour < 12)
                    targetHour += 12;
                if (_settings.RestartAmPm.Equals("AM", StringComparison.OrdinalIgnoreCase) && targetHour == 12)
                    targetHour = 0;

                var target = new DateTime(now.Year, now.Month, now.Day, targetHour, _settings.RestartMinute, 0);
                if (target < now)
                    target = target.AddDays(1);

                double minutesUntil = (target - now).TotalMinutes;
                bool alreadyRanToday = _lastAutoRestartDate.Date == now.Date;

                if (alreadyRanToday)
                    return;

                int[] warnMinutes = { 15, 12, 9, 6, 3 };
                foreach (int warn in warnMinutes)
                {
                    if (minutesUntil <= warn && minutesUntil > warn - 1 && !_sentRestartWarnings.Contains(warn))
                    {
                        _sentRestartWarnings.Add(warn);
                        string msg = $"/announce Server restart in {warn} minutes";
                        _serverProcess.StandardInput.WriteLine(msg);
                        AppendConsole("> " + msg + Environment.NewLine);
                        break;
                    }
                }

                if (minutesUntil <= 0.5 && minutesUntil > -0.5 && !_sentRestartWarnings.Contains(0))
                {
                    _sentRestartWarnings.Add(0);
                    _lastAutoRestartDate = now;
                    string finalMsg = "/announce Server is rebooting now";
                    _serverProcess.StandardInput.WriteLine(finalMsg);
                    AppendConsole("> " + finalMsg + Environment.NewLine);

                    RestartServerButton_Click(this, new RoutedEventArgs());
                }
            };
            _autoRestartTimer.Start();
        }


    }
}
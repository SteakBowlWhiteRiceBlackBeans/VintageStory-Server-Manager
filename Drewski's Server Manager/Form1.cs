// File: Form1.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.Devices;
// test 123
namespace Drewski_s_Server_Manager
{
    public partial class Form1 : Form
    {
        private RichTextBox _console = null!;
        private Panel _titleBar = null!;
        private Label _titleLabel = null!;
        private Button _btnMin = null!;
        private Button _btnMax = null!;
        private Button _btnClose = null!;

        // Right-side controls
        private Panel _rightArea = null!;
        private Label _statusLabel = null!;
        private Panel _statusDot = null!;
        private Label _resourceLabel = null!;
        private Label _playerCountLabel = null!;

        private Button _btnStart = null!;
        private Button _btnStop = null!;
        private Button _btnRestart = null!;
        private Button _btnSave = null!;
        private Button _btnBackup = null!;
        private Button _btnMods = null!;
        private Button _btnSettings = null!;

        private TextBox _commandBox = null!;
        private Button _btnSendCmd = null!;

        // Server process
        private Process? _serverProcess;
        private volatile bool _stopRequested;

        // Settings
        private const string DefaultServerExecutable = "%appdata%/Vintagestory/VintagestoryServer.exe";
        private string _serverExeSetting = DefaultServerExecutable;
        private string _launchArgsSetting = string.Empty;

        // New Settings
        private string _serverDataPathSetting = string.Empty; // VintageStoryServerData root folder
        private bool _autoSaveEnabledSetting;
        private int _autoSaveMinutesSetting = 15;

        private bool _autoBackupEnabledSetting;
        private int _autoBackupMinutesSetting = 60;

        private bool _autoRestartEnabledSetting;
        private int _autoRestartHour12Setting = 6;        // 1-12
        private int _autoRestartMinuteSetting = 0;        // 0-59
        private string _autoRestartAmPmSetting = "AM";    // "AM" / "PM"


        // Settings persistence
        private sealed class AppSettings
        {
            public string ServerExecutable { get; set; } = DefaultServerExecutable;
            public string LaunchArguments { get; set; } = "";

            public string ServerDataPath { get; set; } = "";

            public bool AutoSaveEnabled { get; set; } = false;
            public int AutoSaveMinutes { get; set; } = 15;

            public bool AutoBackupEnabled { get; set; } = false;
            public int AutoBackupMinutes { get; set; } = 60;

            public bool AutoRestartEnabled { get; set; } = false;
            public int AutoRestartHour12 { get; set; } = 6;       // 1-12
            public int AutoRestartMinute { get; set; } = 0;       // 0-59
            public string AutoRestartAmPm { get; set; } = "AM";   // "AM"/"PM"

        }

        private AppSettings _settings = new();

        // Player count (polled via /list clients)
        private readonly System.Windows.Forms.Timer _playerPollTimer = new();
        private readonly object _playerLock = new();
        private bool _capturingListClients;
        private readonly HashSet<string> _capturedPlayers = new(StringComparer.OrdinalIgnoreCase);
        private int _playerCount;

        private static readonly Regex ListClientsLineRegex =
            new(@"\[(?<idx>\d+)\]\s+(?<name>[^\[]+?)\s+\[", RegexOptions.Compiled);

        // Resource polling (CPU/RAM)
        private readonly System.Windows.Forms.Timer _resourceTimer = new();
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private DateTime _lastCpuSampleUtc = DateTime.MinValue;

        // Automation timers
        private readonly System.Windows.Forms.Timer _autoSaveTimer = new();
        private readonly System.Windows.Forms.Timer _autoBackupTimer = new();

        // Auto-restart scheduler (checks clock lightly)
        private readonly System.Windows.Forms.Timer _autoRestartTimer = new();
        private DateTime _lastAutoRestartDate = DateTime.MinValue;   // local date last executed
        private bool _autoRestartInProgress;

        // We only send each countdown message once per day
        private readonly HashSet<int> _restartAnnouncementsSent = new();

        // Announcement command:
        // NOTE (uncertainty): Vintage Story supports various chat/announce commands depending on version/mods.
        // If your server doesn't recognize /announce, change this to the correct command (e.g. /say or /broadcast).
        private const string RestartAnnounceCommand = "/announce";

        private readonly Color _appBg = Color.FromArgb(24, 24, 24);
        private readonly Color _titleBg = Color.FromArgb(40, 40, 40);

        private readonly Color _panelBg = Color.FromArgb(32, 32, 32);
        private readonly Color _textColor = Color.FromArgb(30, 30, 30);

        private const int CornerRadius = 18;

        public Form1()
        {
            InitializeComponent();
            BuildUi();       // must be first so _console exists
            LoadSettings();  // loads config if present

            _playerPollTimer.Interval = 15_000;
            _playerPollTimer.Tick += async (_, __) => await PollPlayerCountAsync();

            _resourceTimer.Interval = 1000; // 1 second
            _resourceTimer.Tick += (_, __) => UpdateResourceUsage();

            _autoSaveTimer.Tick += (_, __) => OnAutoSaveTick();
            _autoBackupTimer.Tick += (_, __) => OnAutoBackupTick();

            // Lightweight clock check; 20s gives good responsiveness without being noisy.
            _autoRestartTimer.Interval = 20_000;
            _autoRestartTimer.Tick += async (_, __) => await CheckAutoRestartAsync();

            // Apply automation scheduling in case settings loaded with enabled options
            ApplyAutomationScheduling(serverRunning: false);
        }

        private void BuildUi()
        {
            Text = "Drewski's Server Manager";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1200, 720);
            BackColor = _appBg;
            FormBorderStyle = FormBorderStyle.None;

            Shown += (_, __) => ApplyRoundedCornersIfNeeded();
            SizeChanged += (_, __) => ApplyRoundedCornersIfNeeded();

            FormClosing += async (_, __) =>
            {
                SaveSettings();
                await StopServerAsync(forceKill: true);
            };

            var root = new Panel { Dock = DockStyle.Fill, BackColor = _appBg };
            Controls.Add(root);

            _titleBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = _titleBg };
            root.Controls.Add(_titleBar);
            _titleBar.MouseDown += TitleBar_MouseDown;

            _titleLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Left,
                Width = 360,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                Text = "Drewski's Server Manager v3.5",
                ForeColor = Color.White,

                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular)
            };
            _titleLabel.MouseDown += TitleBar_MouseDown;
            _titleBar.Controls.Add(_titleLabel);

            var btnPanel = new Panel { Dock = DockStyle.Right, Width = 160, BackColor = _titleBg };
            _titleBar.Controls.Add(btnPanel);

            _btnClose = MakeTitleButton("X");
            _btnClose.Font = new Font("Segoe UI", 10.0f, FontStyle.Regular);
            _btnClose.Click += (_, __) => Close();

            _btnMax = MakeTitleButton("□");
            _btnMax.Font = new Font("Segoe UI", 14.0f, FontStyle.Regular);
            _btnMax.Width = 60;
            _btnMax.Click += (_, __) =>
            {
                WindowState = (WindowState == FormWindowState.Maximized)
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                ApplyRoundedCornersIfNeeded();
            };

            _btnMin = MakeTitleButton("—");
            _btnMin.Click += (_, __) => WindowState = FormWindowState.Minimized;

            btnPanel.Controls.Add(_btnMin);
            btnPanel.Controls.Add(_btnMax);
            btnPanel.Controls.Add(_btnClose);

            var content = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _appBg,
                Padding = new Padding(16, 32, 12, 16)
            };
            root.Controls.Add(content);

            var consoleHost = new Panel
            {
                Width = 720,
                Dock = DockStyle.Left,
                Padding = new Padding(14),
                BackColor = Color.Black
            };

            _rightArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _appBg,
                Padding = new Padding(18, 8, 8, 8),
                AutoScroll = true
            };

            var divider = new Panel
            {
                Dock = DockStyle.Left,
                Width = 1,
                BackColor = Color.FromArgb(210, 210, 210)
            };

            var gap = new Panel
            {
                Dock = DockStyle.Left,
                Width = 16,
                BackColor = _appBg
            };

            content.Controls.Add(_rightArea);
            content.Controls.Add(gap);
            content.Controls.Add(divider);
            content.Controls.Add(consoleHost);

            _console = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                TabStop = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black,
                ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 10.0f),
                DetectUrls = false,
                HideSelection = false,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Cursor = Cursors.Arrow
            };
            _console.KeyPress += (_, e) => e.Handled = true;
            consoleHost.Controls.Add(_console);

            BuildRightPanel(_rightArea);

            AppendConsoleLine("Console ready.");
            SetStatus(ServerStatus.Stopped);
            SetPlayerCount(0);
        }

        private void BuildRightPanel(Panel host)
        {
            host.Controls.Clear();

            // Bottom command panel
            var cmdPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = _appBg,
                Padding = new Padding(0, 8, 0, 0)
            };
            host.Controls.Add(cmdPanel);

            // Top card
            var card = new Panel
            {
                Dock = DockStyle.Top,
                Height = 620,
                BackColor = _panelBg,
                Padding = new Padding(16)
            };
            host.Controls.Add(card);

            // Send button
            _btnSendCmd = new Button
            {
                Text = "Send",
                Dock = DockStyle.Right,
                Width = 90,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(235, 235, 235),
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                TabStop = false
            };
            _btnSendCmd.FlatAppearance.BorderSize = 1;
            _btnSendCmd.FlatAppearance.BorderColor = Color.Black;
            _btnSendCmd.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60);
            _btnSendCmd.FlatAppearance.MouseDownBackColor = Color.FromArgb(75, 75, 75);
            _btnSendCmd.Click += (_, __) => SendCommandFromUi();

            var spacer = new Panel
            {
                Dock = DockStyle.Right,
                Width = 8,
                BackColor = _appBg
            };

            var cmdBoxBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Padding = new Padding(1)
            };

            _commandBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = false,
                Font = new Font("Consolas", 10.0f, FontStyle.Regular),
                ForeColor = Color.FromArgb(230, 230, 230),
                BackColor = Color.FromArgb(30, 30, 30),

                BorderStyle = BorderStyle.None
            };
            _commandBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                SendCommandFromUi();
            };

            cmdBoxBorder.Controls.Add(_commandBox);

            cmdPanel.Controls.Add(cmdBoxBorder);
            cmdPanel.Controls.Add(spacer);
            cmdPanel.Controls.Add(_btnSendCmd);

            // Buttons host (7 buttons)
            var buttonsHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = 7 * 44 + 6 * 10,
                BackColor = _panelBg
            };
            card.Controls.Add(buttonsHost);

            _btnStart = MakeActionButton("Start");
            _btnStart.Click += async (_, __) => await StartServerAsync();

            _btnStop = MakeActionButton("Stop");
            _btnStop.Click += async (_, __) =>
            {
                SetPlayerCount(0);
                await StopServerAsync(forceKill: false);
            };

            _btnRestart = MakeActionButton("Restart");
            _btnRestart.Click += async (_, __) =>
            {
                SetPlayerCount(0);
                await StopServerAsync(forceKill: false);
                await StartServerAsync();
            };

            _btnSave = MakeActionButton("Save");
            _btnSave.Click += (_, __) => SendCommand("/autosavenow", echo: true);

            _btnBackup = MakeActionButton("Backup");
            _btnBackup.Click += (_, __) =>
            {
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                SendCommand($"/genbackup {stamp}", echo: true);
            };

            _btnMods = MakeActionButton("Mods");
            _btnMods.Click += (_, __) => ShowModsDialog();

            _btnSettings = MakeActionButton("Settings");
            _btnSettings.Click += (_, __) => ShowSettingsDialog();

            var buttons = new[] { _btnStart, _btnStop, _btnRestart, _btnSave, _btnBackup, _btnMods, _btnSettings };

            int y = 0;
            foreach (var b in buttons)
            {
                b.Left = 0;
                b.Top = y;
                b.Width = buttonsHost.ClientSize.Width;
                b.Height = 44;
                b.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                buttonsHost.Controls.Add(b);
                y += 44 + 10;
            }

            // Player count row
            var playerRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = _panelBg
            };
            card.Controls.Add(playerRow);

            _playerCountLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "Player Count: 0",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(18, 0, 0, 0)
            };
            playerRow.Controls.Add(_playerCountLabel);

            // Resource usage row (under Status)
            var resourceRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 26,
                BackColor = _panelBg
            };
            card.Controls.Add(resourceRow);

            _resourceLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "CPU: 0%   RAM: 0 MB / 0 MB",
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(18, 0, 0, 0)
            };
            resourceRow.Controls.Add(_resourceLabel);

            // Status row
            var statusRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = _panelBg
            };
            card.Controls.Add(statusRow);

            _statusDot = new Panel
            {
                Width = 12,
                Height = 12,
                Left = 0,
                Top = 13,
                BackColor = Color.Gray
            };
            _statusDot.Paint += (_, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var br = new SolidBrush(_statusDot.BackColor);
                e.Graphics.FillEllipse(br, 0, 0, _statusDot.Width - 1, _statusDot.Height - 1);
            };
            statusRow.Controls.Add(_statusDot);

            _statusLabel = new Label
            {
                AutoSize = false,
                Left = 18,
                Top = 8,
                Width = 360,
                Height = 22,
                Text = "Status: Stopped",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular)
            };
            statusRow.Controls.Add(_statusLabel);

            // Header (added last so it docks above everything)
            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Server Controls",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11.0f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            card.Controls.Add(header);

            SetStatus(ServerStatus.Stopped);
            SetPlayerCount(0);
        }

        private void LoadSettings()
        {
            try
            {
                var path = GetSettingsFilePath();

                SafeLog($"[manager] Config path: {path}");
                SafeLog($"[manager] Config exists: {File.Exists(path)}");

                if (!File.Exists(path))
                    return;

                var json = File.ReadAllText(path);

                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is null)
                {
                    SafeLog("[manager] Config read but deserialize returned null.");
                    return;
                }

                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? exe = null;
                string? args = null;
                string? dataPath = null;

                bool? autoSaveEnabled = null;
                int? autoSaveMinutes = null;

                bool? autoBackupEnabled = null;
                int? autoBackupMinutes = null;

                bool? autoRestartEnabled = null;
                int? autoRestartHour12 = null;
                string? autoRestartAmPm = null;
                int? autoRestartMinute = null;


                if (root.ValueKind == JsonValueKind.Object)
                {
                    exe = TryGetString(root, "ServerExecutable")
                          ?? TryGetString(root, "serverExecutable")
                          ?? TryGetString(root, "ServerExePath")
                          ?? TryGetString(root, "serverExePath")
                          ?? TryGetString(root, "ExePath")
                          ?? TryGetString(root, "exePath");

                    args = TryGetString(root, "LaunchArguments")
                           ?? TryGetString(root, "launchArguments")
                           ?? TryGetString(root, "ServerArgs")
                           ?? TryGetString(root, "serverArgs")
                           ?? TryGetString(root, "Arguments")
                           ?? TryGetString(root, "arguments");

                    dataPath = TryGetString(root, "ServerDataPath")
                               ?? TryGetString(root, "serverDataPath")
                               ?? TryGetString(root, "VintageStoryServerData")
                               ?? TryGetString(root, "vintageStoryServerData");

                    autoSaveEnabled = TryGetBool(root, "AutoSaveEnabled") ?? TryGetBool(root, "autoSaveEnabled");
                    autoSaveMinutes = TryGetInt(root, "AutoSaveMinutes") ?? TryGetInt(root, "autoSaveMinutes");

                    autoBackupEnabled = TryGetBool(root, "AutoBackupEnabled") ?? TryGetBool(root, "autoBackupEnabled");
                    autoBackupMinutes = TryGetInt(root, "AutoBackupMinutes") ?? TryGetInt(root, "autoBackupMinutes");

                    autoRestartEnabled = TryGetBool(root, "AutoRestartEnabled") ?? TryGetBool(root, "autoRestartEnabled");
                    autoRestartHour12 = TryGetInt(root, "AutoRestartHour12") ?? TryGetInt(root, "autoRestartHour12");
                    autoRestartMinute = TryGetInt(root, "AutoRestartMinute") ?? TryGetInt(root, "autoRestartMinute");
                    autoRestartAmPm = TryGetString(root, "AutoRestartAmPm") ?? TryGetString(root, "autoRestartAmPm");
                }

                _settings = loaded;

                _serverExeSetting = !string.IsNullOrWhiteSpace(exe)
                    ? exe!
                    : (!string.IsNullOrWhiteSpace(_settings.ServerExecutable) ? _settings.ServerExecutable : DefaultServerExecutable);

                _launchArgsSetting = args ?? (_settings.LaunchArguments ?? string.Empty);

                _serverDataPathSetting = !string.IsNullOrWhiteSpace(dataPath)
                    ? dataPath!
                    : (_settings.ServerDataPath ?? string.Empty);

                _autoSaveEnabledSetting = autoSaveEnabled ?? _settings.AutoSaveEnabled;
                _autoSaveMinutesSetting = ClampMinutes(autoSaveMinutes ?? _settings.AutoSaveMinutes, 15);

                _autoBackupEnabledSetting = autoBackupEnabled ?? _settings.AutoBackupEnabled;
                _autoBackupMinutesSetting = ClampMinutes(autoBackupMinutes ?? _settings.AutoBackupMinutes, 60);

                _autoRestartEnabledSetting = autoRestartEnabled ?? _settings.AutoRestartEnabled;
                _autoRestartHour12Setting = ClampHour12(autoRestartHour12 ?? _settings.AutoRestartHour12, 6);
                _autoRestartMinuteSetting = ClampMinute(autoRestartMinute ?? _settings.AutoRestartMinute, 0);
                _autoRestartAmPmSetting = NormalizeAmPm(autoRestartAmPm ?? _settings.AutoRestartAmPm);


                SafeLog("[manager] Settings loaded and applied.");
                SafeLog($"[manager] Loaded EXE: {_serverExeSetting}");
                if (!string.IsNullOrWhiteSpace(_launchArgsSetting))
                    SafeLog($"[manager] Loaded Args: {_launchArgsSetting}");
                if (!string.IsNullOrWhiteSpace(_serverDataPathSetting))
                    SafeLog($"[manager] Loaded ServerDataPath: {_serverDataPathSetting}");
            }
            catch (Exception ex)
            {
                SafeLog("[manager] Failed to load settings: " + ex.Message);
            }
        }

        private static string? TryGetString(JsonElement obj, string propName)
        {
            if (!obj.TryGetProperty(propName, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static bool? TryGetBool(JsonElement obj, string propName)
        {
            if (!obj.TryGetProperty(propName, out var v)) return null;
            return v.ValueKind == JsonValueKind.True ? true :
                   v.ValueKind == JsonValueKind.False ? false : (bool?)null;
        }

        private static int? TryGetInt(JsonElement obj, string propName)
        {
            if (!obj.TryGetProperty(propName, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        private static int ClampMinutes(int value, int fallback) => (value >= 1 && value <= 999) ? value : fallback;
        private static int ClampHour12(int value, int fallback) => (value >= 1 && value <= 12) ? value : fallback;
        private static int ClampMinute(int value, int fallback) => (value >= 0 && value <= 59) ? value : fallback;


        private static string NormalizeAmPm(string value)
        {
            if (string.Equals(value, "PM", StringComparison.OrdinalIgnoreCase)) return "PM";
            return "AM";
        }

        private void SafeLog(string msg)
        {
            if (_console is null || IsDisposed) return;
            AppendConsoleLine(msg);
        }

        private void SaveSettings()
        {
            try
            {
                _settings.ServerExecutable = _serverExeSetting;
                _settings.LaunchArguments = _launchArgsSetting;

                _settings.ServerDataPath = _serverDataPathSetting;

                _settings.AutoSaveEnabled = _autoSaveEnabledSetting;
                _settings.AutoSaveMinutes = ClampMinutes(_autoSaveMinutesSetting, 15);

                _settings.AutoBackupEnabled = _autoBackupEnabledSetting;
                _settings.AutoBackupMinutes = ClampMinutes(_autoBackupMinutesSetting, 60);

                _settings.AutoRestartEnabled = _autoRestartEnabledSetting;
                _settings.AutoRestartHour12 = ClampHour12(_autoRestartHour12Setting, 6);
                _settings.AutoRestartMinute = ClampMinute(_autoRestartMinuteSetting, 0);
                _settings.AutoRestartAmPm = NormalizeAmPm(_autoRestartAmPmSetting);

                var path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                AppendConsoleLine("[manager] Failed to save settings: " + ex.Message);
            }
        }

        private void ShowSettingsDialog()
        {
            using var dlg = new SettingsForm(
                _serverExeSetting,
                _launchArgsSetting,
                _serverDataPathSetting,
                _autoSaveEnabledSetting,
                _autoSaveMinutesSetting,
                _autoBackupEnabledSetting,
                _autoBackupMinutesSetting,
                _autoRestartEnabledSetting,
                _autoRestartHour12Setting,
                _autoRestartMinuteSetting,
                _autoRestartAmPmSetting);

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            _serverExeSetting = dlg.ServerExecutable;
            _launchArgsSetting = dlg.LaunchArguments;

            _serverDataPathSetting = dlg.ServerDataPath;

            _autoSaveEnabledSetting = dlg.AutoSaveEnabled;
            _autoSaveMinutesSetting = dlg.AutoSaveMinutes;

            _autoBackupEnabledSetting = dlg.AutoBackupEnabled;
            _autoBackupMinutesSetting = dlg.AutoBackupMinutes;

            _autoRestartEnabledSetting = dlg.AutoRestartEnabled;
            _autoRestartHour12Setting = dlg.AutoRestartHour12;
            _autoRestartMinuteSetting = dlg.AutoRestartMinute;
            _autoRestartAmPmSetting = dlg.AutoRestartAmPm;


            SaveSettings();

            AppendConsoleLine("[manager] Settings saved.");
            AppendConsoleLine($"[manager] Server Executable: {_serverExeSetting}");
            if (!string.IsNullOrWhiteSpace(_launchArgsSetting))
                AppendConsoleLine($"[manager] Launch Arguments: {_launchArgsSetting}");
            if (!string.IsNullOrWhiteSpace(_serverDataPathSetting))
                AppendConsoleLine($"[manager] ServerDataPath: {_serverDataPathSetting}");

            ApplyAutomationScheduling(serverRunning: IsServerRunning());
        }

        private void ShowModsDialog()
        {
            if (string.IsNullOrWhiteSpace(_serverDataPathSetting))
            {
                MessageBox.Show(this,
                    "Set the VintageStoryServerData folder location in Settings first.",
                    "ServerDataPath not set",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var dataRoot = ResolvePath(_serverDataPathSetting);
            var modsPath = Path.Combine(dataRoot, "Mods");

            using var dlg = new ModsForm(modsPath);
            dlg.ShowDialog(this);
        }

        private void SendCommandFromUi()
        {
            var cmd = _commandBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(cmd)) return;

            _commandBox.Clear();
            SendCommand(cmd, echo: true);
        }

        private void SendCommand(string cmd, bool echo)
        {
            if (echo) AppendConsoleLine($"> {cmd}");

            if (!IsServerRunning())
            {
                AppendConsoleLine("[manager] Server is not running.");
                SetStatus(ServerStatus.Stopped);
                return;
            }

            try
            {
                _serverProcess!.StandardInput.WriteLine(cmd);
                _serverProcess.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                AppendConsoleLine("[manager] Failed to send command: " + ex.Message);
            }
        }

        private async Task StartServerAsync()
        {
            if (IsServerRunning())
            {
                AppendConsoleLine("[manager] Server is already running.");
                SetStatus(ServerStatus.Running);
                return;
            }

            var exePath = ResolvePath(_serverExeSetting);
            if (!File.Exists(exePath))
            {
                AppendConsoleLine($"[manager] EXE not found: {exePath}");
                SetStatus(ServerStatus.Crashed);
                return;
            }

            // Enforce ServerDataPath via Settings only
            if (LaunchArgsContainDataPath(_launchArgsSetting))
            {
                MessageBox.Show(
                    this,
                    "Please remove --datapath and use the folder selection instead.",
                    "Invalid Launch Arguments",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                AppendConsoleLine("[manager] Launch blocked: --datapath must be set via Settings.");
                SetStatus(ServerStatus.Stopped);
                return;
            }

            try
            {
                _stopRequested = false;

                var workingDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = BuildEffectiveLaunchArguments(),
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                };

                AppendConsoleLine($"[manager] Launching: \"{psi.FileName}\" {psi.Arguments}");

                var p = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                p.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    if (ShouldSuppressPlayerPollLine(e.Data)) return;
                    UiAppendConsoleLine(e.Data);
                };

                p.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    UiAppendConsoleLine("[err] " + e.Data);
                };

                p.Exited += (_, __) =>
                {
                    var stoppedByUser = _stopRequested;
                    UiAppendConsoleLine($"[manager] Server exited (code {SafeExitCode(p)}).");

                    if (stoppedByUser)
                        BeginInvoke(new Action(() => SetStatus(ServerStatus.Stopped)));
                    else
                        BeginInvoke(new Action(() => SetStatus(ServerStatus.Crashed)));
                };

                if (!p.Start())
                {
                    AppendConsoleLine("[manager] Failed to start server.");
                    SetStatus(ServerStatus.Crashed);
                    return;
                }

                _serverProcess = p;

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                AppendConsoleLine("[manager] Server started.");
                SetStatus(ServerStatus.Running);

                // reset per-day restart sent messages when server starts (optional)
                // (we keep per-day, but this helps if you start/stop around the window)
                _ = PollPlayerCountAsync();
            }
            catch (Exception ex)
            {
                AppendConsoleLine("[manager] Start failed: " + ex.Message);
                SetStatus(ServerStatus.Crashed);
            }

            await Task.CompletedTask;
        }

        private async Task StopServerAsync(bool forceKill)
        {
            if (!IsServerRunning())
            {
                SetStatus(ServerStatus.Stopped);
                AppendConsoleLine("[manager] Server is not running.");
                return;
            }

            try
            {
                _stopRequested = true;

                if (_serverProcess is null)
                {
                    SetStatus(ServerStatus.Stopped);
                    return;
                }

                AppendConsoleLine("[manager] Sending /stop ...");

                if (!forceKill)
                {
                    try
                    {
                        _serverProcess.StandardInput.WriteLine("/stop");
                        _serverProcess.StandardInput.Flush();
                    }
                    catch (Exception ex)
                    {
                        AppendConsoleLine("[manager] Failed to write to stdin: " + ex.Message);
                    }

                    var exited = await WaitForExitAsync(_serverProcess, TimeSpan.FromSeconds(10));
                    if (exited)
                    {
                        SetStatus(ServerStatus.Stopped);
                        AppendConsoleLine("[manager] Server stopped.");
                        return;
                    }

                    AppendConsoleLine("[manager] stop timeout; forcing close...");
                }

                _serverProcess.Kill(entireProcessTree: true);
                await WaitForExitAsync(_serverProcess, TimeSpan.FromSeconds(5));

                SetStatus(ServerStatus.Stopped);
                AppendConsoleLine("[manager] Server stopped (killed).");
            }
            catch (Exception ex)
            {
                AppendConsoleLine("[manager] Stop failed: " + ex.Message);
                SetStatus(ServerStatus.Crashed);
            }
        }

        private async Task PollPlayerCountAsync()
        {
            if (!IsServerRunning()) return;
            if (_capturingListClients) return;

            _capturingListClients = true;
            try
            {
                lock (_playerLock)
                {
                    _capturedPlayers.Clear();
                }

                SendCommand("/list clients", echo: false);

                await Task.Delay(1500);

                int count;
                lock (_playerLock)
                {
                    count = _capturedPlayers.Count;
                }

                SetPlayerCount(count);
            }
            finally
            {
                _capturingListClients = false;
            }
        }

        private bool ShouldSuppressPlayerPollLine(string line)
        {
            if (_capturingListClients)
            {
                var trimmed = line.Trim();

                if (trimmed.Contains("Handling Console Command /list clients", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (trimmed.Contains("List of online Players", StringComparison.OrdinalIgnoreCase))
                    return true;

                var m = ListClientsLineRegex.Match(trimmed);
                if (m.Success)
                {
                    var name = m.Groups["name"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        lock (_playerLock)
                        {
                            _capturedPlayers.Add(name);
                        }
                    }

                    return true;
                }
            }

            return false;
        }

        private void SetPlayerCount(int count)
        {
            count = Math.Max(0, count);
            _playerCount = count;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => _playerCountLabel.Text = $"Player Count: {_playerCount}"));
                return;
            }

            _playerCountLabel.Text = $"Player Count: {_playerCount}";
        }

        private static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await p.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return p.HasExited;
            }
            catch
            {
                return p.HasExited;
            }
        }

        private bool IsServerRunning() => _serverProcess is { HasExited: false };

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; }
            catch { return -1; }
        }

        private static string ResolvePath(string path)
        {
            var normalized = (path ?? string.Empty).Replace('/', '\\');
            return Environment.ExpandEnvironmentVariables(normalized);
        }

        private string GetSettingsFilePath()
        {
            // Prefer ServerDataPath if set
            if (!string.IsNullOrWhiteSpace(_serverDataPathSetting))
            {
                var dataRoot = ResolvePath(_serverDataPathSetting);
                return Path.Combine(
                    dataRoot,
                    "ModConfig",
                    "DrewskiServerManager.json");
            }

            // Fallback (used before data path is configured)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VintagestoryData",
                "ModConfig",
                "DrewskiServerManager.json");
        }


        private void UiAppendConsoleLine(string line)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendConsoleLine(line)));
                return;
            }

            AppendConsoleLine(line);
        }

        private void SetStatus(ServerStatus status)
        {
            var (text, dot) = status switch
            {
                ServerStatus.Running => ("Status: Running", Color.FromArgb(60, 160, 90)),
                ServerStatus.Stopped => ("Status: Stopped", Color.Gray),
                ServerStatus.Crashed => ("Status: Crashed", Color.FromArgb(200, 70, 70)),
                _ => ("Status: Unknown", Color.Gray)
            };

            _statusLabel.Text = text;
            _statusDot.BackColor = dot;
            _statusDot.Invalidate();

            if (status == ServerStatus.Running)
            {
                if (!_playerPollTimer.Enabled) _playerPollTimer.Start();

                if (!_resourceTimer.Enabled)
                {
                    _lastCpuTime = TimeSpan.Zero;
                    _lastCpuSampleUtc = DateTime.UtcNow;
                    _resourceTimer.Start();
                }

                ApplyAutomationScheduling(serverRunning: true);
            }
            else
            {
                if (_playerPollTimer.Enabled) _playerPollTimer.Stop();
                if (_resourceTimer.Enabled) _resourceTimer.Stop();

                if (_resourceLabel != null)
                    _resourceLabel.Text = "CPU: 0%   RAM: 0 MB / 0 MB";

                ApplyAutomationScheduling(serverRunning: false);
                SetPlayerCount(0);
            }
        }

        private enum ServerStatus
        {
            Unknown,
            Running,
            Stopped,
            Crashed
        }

        private Button MakeTitleButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                Dock = DockStyle.Right,
                Width = 50,
                Height = 44,
                FlatStyle = FlatStyle.Flat,
                BackColor = _titleBg,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular),
                TabStop = false
            };

            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 48);
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(56, 56, 56);


            return btn;
        }

        private Button MakeActionButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(50, 50, 50),        // dark default
                ForeColor = Color.FromArgb(230, 230, 230),    // light text
                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false
            };

            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60); // hover
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(75, 75, 75); // click

            return btn;
        }


        private void AppendConsoleLine(string line)
        {
            _console.AppendText(line + Environment.NewLine);
            _console.SelectionStart = _console.TextLength;
            _console.ScrollToCaret();
        }

        private void ApplyRoundedCornersIfNeeded()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                Region = null;
                return;
            }

            IntPtr region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, CornerRadius, CornerRadius);
            Region = Region.FromHrgn(region);
        }

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        // Build final launch args. Adds --dataPath from Settings automatically.
        private string BuildEffectiveLaunchArguments()
        {
            var args = (_launchArgsSetting ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(_serverDataPathSetting))
            {
                var dp = ResolvePath(_serverDataPathSetting).Trim();
                dp = dp.Replace("\"", ""); // prevent quote breaking

                args = string.IsNullOrWhiteSpace(args)
                    ? $"--dataPath \"{dp}\""
                    : $"{args} --dataPath \"{dp}\"";
            }

            return args;
        }

        // Detect manual --datapath usage in Launch Arguments (blocked)
        private bool LaunchArgsContainDataPath(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return false;

            return Regex.IsMatch(
                args,
                @"(^|\s)--datapath\b",
                RegexOptions.IgnoreCase
            );
        }

        // Lightweight server process resource usage (CPU% and RAM used/total)
        private void UpdateResourceUsage()
        {
            if (_resourceLabel == null) return;

            if (_serverProcess == null || _serverProcess.HasExited)
            {
                _resourceLabel.Text = "CPU: 0%   RAM: 0 MB / 0 MB";
                return;
            }

            try
            {
                var now = DateTime.UtcNow;
                var cpuTime = _serverProcess.TotalProcessorTime;

                double cpuPercent = 0;

                if (_lastCpuTime != TimeSpan.Zero && _lastCpuSampleUtc != DateTime.MinValue)
                {
                    var cpuUsedMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                    var elapsedMs = (now - _lastCpuSampleUtc).TotalMilliseconds;

                    if (elapsedMs > 0)
                    {
                        cpuPercent = cpuUsedMs / (elapsedMs * Environment.ProcessorCount) * 100.0;
                        cpuPercent = Math.Max(0, Math.Min(100, cpuPercent));
                    }
                }

                _lastCpuTime = cpuTime;
                _lastCpuSampleUtc = now;

                var usedRamMb = _serverProcess.WorkingSet64 / (1024 * 1024);
                var totalRamMb = GetTotalSystemRamMb();

                _resourceLabel.Text = $"CPU: {cpuPercent:0}%   RAM: {usedRamMb:N0} MB / {totalRamMb:N0} MB";
            }
            catch
            {
                // never crash UI due to resource polling
            }
        }

        private static long GetTotalSystemRamMb()
        {
            try
            {
                ulong totalBytes = new ComputerInfo().TotalPhysicalMemory;
                ulong totalMb = totalBytes / (1024UL * 1024UL);
                return (long)totalMb;
            }
            catch
            {
                return 0;
            }
        }

        // ==========================
        // AUTOMATION IMPLEMENTATION
        // ==========================
        private void ApplyAutomationScheduling(bool serverRunning)
        {
            // Auto Save timer
            if (serverRunning && _autoSaveEnabledSetting && _autoSaveMinutesSetting >= 1 && _autoSaveMinutesSetting <= 999)
            {
                _autoSaveTimer.Interval = _autoSaveMinutesSetting * 60_000;
                if (!_autoSaveTimer.Enabled) _autoSaveTimer.Start();
            }
            else
            {
                if (_autoSaveTimer.Enabled) _autoSaveTimer.Stop();
            }

            // Auto Backup timer
            if (serverRunning && _autoBackupEnabledSetting && _autoBackupMinutesSetting >= 1 && _autoBackupMinutesSetting <= 999)
            {
                _autoBackupTimer.Interval = _autoBackupMinutesSetting * 60_000;
                if (!_autoBackupTimer.Enabled) _autoBackupTimer.Start();
            }
            else
            {
                if (_autoBackupTimer.Enabled) _autoBackupTimer.Stop();
            }

            // Auto Restart scheduler
            if (serverRunning && _autoRestartEnabledSetting)
            {
                if (!_autoRestartTimer.Enabled) _autoRestartTimer.Start();
            }
            else
            {
                if (_autoRestartTimer.Enabled) _autoRestartTimer.Stop();
                _restartAnnouncementsSent.Clear();
                _autoRestartInProgress = false;
            }
        }

        private void OnAutoSaveTick()
        {
            if (!IsServerRunning()) return;
            if (!_autoSaveEnabledSetting) return;

            // Prevent reentrancy if tick overlaps (rare, but safe)
            _autoSaveTimer.Stop();
            try
            {
                SendCommand("/autosavenow", echo: true);
            }
            finally
            {
                if (IsServerRunning() && _autoSaveEnabledSetting)
                    _autoSaveTimer.Start();
            }
        }

        private void OnAutoBackupTick()
        {
            if (!IsServerRunning()) return;
            if (!_autoBackupEnabledSetting) return;

            _autoBackupTimer.Stop();
            try
            {
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                SendCommand($"/genbackup {stamp}", echo: true);
            }
            finally
            {
                if (IsServerRunning() && _autoBackupEnabledSetting)
                    _autoBackupTimer.Start();
            }
        }

        private async Task CheckAutoRestartAsync()
        {
            if (!IsServerRunning()) return;
            if (!_autoRestartEnabledSetting) return;
            if (_autoRestartInProgress) return;

            // Build today's scheduled time (local)
            var now = DateTime.Now;
            var scheduled = BuildScheduledRestartTime(now.Date);

            // New day: clear sent announcements
            if (_restartAnnouncementsSent.Count > 0 && _lastAutoRestartDate.Date != now.Date)
            {
                _restartAnnouncementsSent.Clear();
            }

            // Only announce within the 15-minute window BEFORE restart
            var windowStart = scheduled.AddMinutes(-15);

            if (now >= windowStart && now <= scheduled)
            {
                var minutesRemaining = (int)Math.Ceiling((scheduled - now).TotalMinutes);
                if (minutesRemaining < 0) minutesRemaining = 0;

                // 15 minutes prior and every 3 minutes after (15,12,9,6,3,0)
                if (minutesRemaining == 15 ||
                    minutesRemaining == 12 ||
                    minutesRemaining == 9 ||
                    minutesRemaining == 6 ||
                    minutesRemaining == 3 ||
                    minutesRemaining == 0)
                {
                    if (!_restartAnnouncementsSent.Contains(minutesRemaining))
                    {
                        _restartAnnouncementsSent.Add(minutesRemaining);

                        var msg = minutesRemaining == 0
                            ? "Server is rebooting now"
                            : $"Server is rebooting in {minutesRemaining} minutes";

                        // NOTE (uncertainty): If /announce isn't correct for your server, change RestartAnnounceCommand constant.
                        SendCommand($"{RestartAnnounceCommand} {msg}", echo: true);
                    }
                }
            }

            // Execute restart at/after scheduled time, once per day
            if (now >= scheduled && _lastAutoRestartDate.Date != now.Date)
            {
                _autoRestartInProgress = true;
                try
                {
                    await RunAutoRestartAsync();
                    _lastAutoRestartDate = now.Date;
                    _restartAnnouncementsSent.Clear();
                }
                finally
                {
                    _autoRestartInProgress = false;
                }
            }
        }

        private DateTime BuildScheduledRestartTime(DateTime dayLocalDate)
        {
            var hour12 = ClampHour12(_autoRestartHour12Setting, 6);
            var minute = ClampMinute(_autoRestartMinuteSetting, 0);
            var ampm = NormalizeAmPm(_autoRestartAmPmSetting);

            int hour24 = hour12 % 12; // 12 -> 0
            if (ampm == "PM") hour24 += 12;

            return new DateTime(dayLocalDate.Year, dayLocalDate.Month, dayLocalDate.Day, hour24, minute, 0, DateTimeKind.Local);
        }


        private async Task RunAutoRestartAsync()
        {
            if (!IsServerRunning()) return;

            AppendConsoleLine("[manager] Auto Restart: issuing save...");
            SendCommand("/autosavenow", echo: true);

            // You asked: wait 5 seconds after save completes.
            // NOTE (uncertainty): We don't have a deterministic "save completed" event yet, so we wait 5 seconds.
            await Task.Delay(5000);

            AppendConsoleLine("[manager] Auto Restart: restarting server...");
            await StopServerAsync(forceKill: false);
            await StartServerAsync();
        }

        // ==========================
        // WIN32 INTEROP
        // ==========================
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse);

        // ==========================
        // SETTINGS WINDOW
        // ==========================
        private sealed class SettingsForm : Form
        {
            private readonly TextBox _exeBox;
            private readonly TextBox _argsBox;

            private readonly TextBox _dataPathBox;

            private readonly CheckBox _autoSaveChk;
            private readonly TextBox _autoSaveMinutesBox;

            private readonly CheckBox _autoBackupChk;
            private readonly TextBox _autoBackupMinutesBox;

            private readonly CheckBox _autoRestartChk;
            private readonly ComboBox _restartHourBox;
            private readonly ComboBox _restartMinuteBox;
            private readonly ComboBox _restartAmPmBox;
            private readonly Label _restartNote;

            public string ServerExecutable => _exeBox.Text.Trim();
            public string LaunchArguments => _argsBox.Text;

            public string ServerDataPath => _dataPathBox.Text.Trim();

            public bool AutoSaveEnabled => _autoSaveChk.Checked;
            public int AutoSaveMinutes => ParseMinutesOrDefault(_autoSaveMinutesBox.Text, 15);

            public bool AutoBackupEnabled => _autoBackupChk.Checked;
            public int AutoBackupMinutes => ParseMinutesOrDefault(_autoBackupMinutesBox.Text, 60);

            public bool AutoRestartEnabled => _autoRestartChk.Checked;
            public int AutoRestartHour12 => ParseHour12OrDefault(_restartHourBox.SelectedItem?.ToString(), 6);
            public string AutoRestartAmPm => NormalizeAmPm(_restartAmPmBox.SelectedItem?.ToString());
            public int AutoRestartMinute => ParseMinuteOrDefault(_restartMinuteBox.SelectedItem?.ToString(), 0);


            public SettingsForm(
                string exeValue,
                string argsValue,
                string dataPathValue,
                bool autoSaveEnabled,
                int autoSaveMinutes,
                bool autoBackupEnabled,
                int autoBackupMinutes,
                bool autoRestartEnabled,
                int autoRestartHour12,
                int autoRestartMinute,
                string autoRestartAmPm)

            {
                Text = "Settings";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = false;
                MaximizeBox = true;
                ShowInTaskbar = false;

                MinimumSize = new Size(700, 520);
                ClientSize = new Size(820, 580);

                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };
                Controls.Add(root);

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 18
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                // EXE
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));

                // ARGS
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));

                // DATAPATH
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));

                // AUTOMATION header
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

                // autosave, autobackup
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

                // auto restart row + note
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

                // filler + buttons
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

                root.Controls.Add(layout);

                // Server Executable
                layout.Controls.Add(new Label
                {
                    Text = "Server Executable",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.BottomLeft
                }, 0, 0);

                var exeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
                exeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                exeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
                layout.Controls.Add(exeRow, 0, 1);

                _exeBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Text = string.IsNullOrWhiteSpace(exeValue) ? DefaultServerExecutable : exeValue
                };
                exeRow.Controls.Add(_exeBox, 0, 0);

                var btnBrowseExe = new Button { Text = "Browse...", Dock = DockStyle.Fill };
                btnBrowseExe.Click += (_, __) => BrowseForExe();
                exeRow.Controls.Add(btnBrowseExe, 1, 0);

                // Launch Arguments
                layout.Controls.Add(new Label
                {
                    Text = "Launch Arguments",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.BottomLeft
                }, 0, 3);

                var argsPanel = new Panel { Dock = DockStyle.Fill };

                _argsBox = new TextBox
                {
                    Dock = DockStyle.Top,
                    Height = 24,
                    Text = argsValue ?? string.Empty
                };

                var argsNote = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 26,
                    Text = "Selecting a new server data folder location will automatically include the argument --datapath \"server data folder\"",
                    ForeColor = Color.FromArgb(90, 90, 90),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                    Padding = new Padding(2, 6, 2, 0)
                };

                argsPanel.Controls.Add(argsNote);
                argsPanel.Controls.Add(_argsBox);
                layout.Controls.Add(argsPanel, 0, 4);

                // VintageStoryServerData folder location
                layout.Controls.Add(new Label
                {
                    Text = "VintageStoryServerData folder location",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.BottomLeft
                }, 0, 6);

                var dataRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
                dataRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                dataRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
                layout.Controls.Add(dataRow, 0, 7);

                _dataPathBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Text = dataPathValue ?? string.Empty
                };
                dataRow.Controls.Add(_dataPathBox, 0, 0);

                var btnBrowseData = new Button { Text = "Browse...", Dock = DockStyle.Fill };
                btnBrowseData.Click += (_, __) => BrowseForFolder(_dataPathBox);
                dataRow.Controls.Add(btnBrowseData, 1, 0);

                // Automation label
                layout.Controls.Add(new Label
                {
                    Text = "Automation",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.BottomLeft
                }, 0, 9);

                // Auto Save row
                var autoSaveRow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    Padding = new Padding(0),
                    AutoSize = false
                };
                layout.Controls.Add(autoSaveRow, 0, 10);

                _autoSaveChk = new CheckBox
                {
                    Text = "Auto Save",
                    Checked = autoSaveEnabled,
                    AutoSize = true,
                    Margin = new Padding(0, 6, 12, 0)
                };
                autoSaveRow.Controls.Add(_autoSaveChk);

                autoSaveRow.Controls.Add(new Label
                {
                    Text = "minutes",
                    AutoSize = true,
                    Margin = new Padding(0, 8, 6, 0)
                });

                _autoSaveMinutesBox = new TextBox
                {
                    Width = 70,
                    Text = ClampTextMinutes(autoSaveMinutes, 15),
                    Margin = new Padding(0, 5, 0, 0)
                };
                _autoSaveMinutesBox.KeyPress += Minutes_KeyPressNumbersOnly;
                autoSaveRow.Controls.Add(_autoSaveMinutesBox);

                // Auto Backup row
                var autoBackupRow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    Padding = new Padding(0),
                    AutoSize = false
                };
                layout.Controls.Add(autoBackupRow, 0, 11);

                _autoBackupChk = new CheckBox
                {
                    Text = "Auto Backup",
                    Checked = autoBackupEnabled,
                    AutoSize = true,
                    Margin = new Padding(0, 6, 12, 0)
                };
                autoBackupRow.Controls.Add(_autoBackupChk);

                autoBackupRow.Controls.Add(new Label
                {
                    Text = "minutes",
                    AutoSize = true,
                    Margin = new Padding(0, 8, 6, 0)
                });

                _autoBackupMinutesBox = new TextBox
                {
                    Width = 70,
                    Text = ClampTextMinutes(autoBackupMinutes, 60),
                    Margin = new Padding(0, 5, 0, 0)
                };
                _autoBackupMinutesBox.KeyPress += Minutes_KeyPressNumbersOnly;
                autoBackupRow.Controls.Add(_autoBackupMinutesBox);

                // Auto Restart row (checkbox + time dropdowns)
                var autoRestartRow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    Padding = new Padding(0),
                    AutoSize = false
                };
                layout.Controls.Add(autoRestartRow, 0, 12);

                _autoRestartChk = new CheckBox
                {
                    Text = "Auto Restart",
                    Checked = autoRestartEnabled,
                    AutoSize = true,
                    Margin = new Padding(0, 6, 12, 0)
                };
                autoRestartRow.Controls.Add(_autoRestartChk);

                autoRestartRow.Controls.Add(new Label
                {
                    Text = "Time",
                    AutoSize = true,
                    Margin = new Padding(0, 8, 6, 0)
                });

                _restartHourBox = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width = 70,
                    Margin = new Padding(0, 5, 0, 0)
                };
                for (int h = 1; h <= 12; h++) _restartHourBox.Items.Add(h.ToString());
                _restartHourBox.SelectedItem = ClampHour12Text(autoRestartHour12, 6);
                autoRestartRow.Controls.Add(_restartHourBox);

                autoRestartRow.Controls.Add(new Label
                {
                    Text = ":",
                    AutoSize = true,
                    Margin = new Padding(6, 8, 6, 0)
                });

                _restartMinuteBox = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width = 70,
                    Margin = new Padding(0, 5, 8, 0)
                };
                for (int m = 0; m <= 59; m++) _restartMinuteBox.Items.Add(m.ToString("00"));
                _restartMinuteBox.SelectedItem = ClampMinuteText(autoRestartMinute, 0);
                autoRestartRow.Controls.Add(_restartMinuteBox);

                _restartAmPmBox = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width = 70,
                    Margin = new Padding(0, 5, 0, 0)
                };
                _restartAmPmBox.Items.Add("AM");
                _restartAmPmBox.Items.Add("PM");
                _restartAmPmBox.SelectedItem = NormalizeAmPm(autoRestartAmPm);
                autoRestartRow.Controls.Add(_restartAmPmBox);


                // Auto Restart note row (shows only when enabled)
                _restartNote = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "Reboot announcement will be sent in chat 15 minutes prior and every 3 minutes after",
                    ForeColor = Color.FromArgb(90, 90, 90),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                    Padding = new Padding(2, 2, 2, 0),
                    Visible = autoRestartEnabled
                };
                layout.Controls.Add(_restartNote, 0, 13);

                // Enable/disable dependent inputs
                _autoSaveChk.CheckedChanged += (_, __) => _autoSaveMinutesBox.Enabled = _autoSaveChk.Checked;
                _autoBackupChk.CheckedChanged += (_, __) => _autoBackupMinutesBox.Enabled = _autoBackupChk.Checked;

                _autoRestartChk.CheckedChanged += (_, __) =>
                {
                    _restartHourBox.Enabled = _autoRestartChk.Checked;
                    _restartMinuteBox.Enabled = _autoRestartChk.Checked;
                    _restartAmPmBox.Enabled = _autoRestartChk.Checked;
                    _restartNote.Visible = _autoRestartChk.Checked;
                };

                _autoSaveMinutesBox.Enabled = _autoSaveChk.Checked;
                _autoBackupMinutesBox.Enabled = _autoBackupChk.Checked;

                _restartHourBox.Enabled = _autoRestartChk.Checked;
                _restartMinuteBox.Enabled = _autoRestartChk.Checked;
                _restartAmPmBox.Enabled = _autoRestartChk.Checked;

                // Buttons row
                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(0),
                    WrapContents = false
                };
                layout.Controls.Add(buttons, 0, 15);

                var btnSave = new Button
                {
                    Text = "Save",
                    DialogResult = DialogResult.OK,
                    Width = 100,
                    Height = 32,
                    Margin = new Padding(8, 6, 0, 6)
                };

                btnSave.Click += (_, __) =>
                {
                    // Block manual --datapath usage
                    if (Regex.IsMatch(_argsBox.Text ?? string.Empty, @"(^|\s)--datapath\b", RegexOptions.IgnoreCase))
                    {
                        MessageBox.Show(
                            this,
                            "Please remove --datapath and use the folder selection instead.",
                            "Invalid Launch Arguments",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );

                        DialogResult = DialogResult.None;
                        _argsBox.Focus();
                        return;
                    }

                    if (_autoSaveChk.Checked && !IsValidMinutes(_autoSaveMinutesBox.Text))
                    {
                        MessageBox.Show(this,
                            "Auto Save minutes must be a number from 1 to 999.",
                            "Invalid Value",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);

                        DialogResult = DialogResult.None;
                        _autoSaveMinutesBox.Focus();
                        return;
                    }

                    if (_autoBackupChk.Checked && !IsValidMinutes(_autoBackupMinutesBox.Text))
                    {
                        MessageBox.Show(this,
                            "Auto Backup minutes must be a number from 1 to 999.",
                            "Invalid Value",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);

                        DialogResult = DialogResult.None;
                        _autoBackupMinutesBox.Focus();
                        return;
                    }

                    if (_autoRestartChk.Checked)
                    {
                        if (_restartHourBox.SelectedItem is null)
                        {
                            MessageBox.Show(this,
                                "Select an Auto Restart time (hour).",
                                "Invalid Value",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);

                            DialogResult = DialogResult.None;
                            _restartHourBox.Focus();
                            return;
                        }

                        // ADD THIS BLOCK
                        if (_restartMinuteBox.SelectedItem is null)
                        {
                            MessageBox.Show(this,
                                "Select an Auto Restart time (minutes).",
                                "Invalid Value",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);

                            DialogResult = DialogResult.None;
                            _restartMinuteBox.Focus();
                            return;
                        }

                        if (_restartAmPmBox.SelectedItem is null)
                        {
                            MessageBox.Show(this,
                                "Select AM or PM for Auto Restart.",
                                "Invalid Value",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);

                            DialogResult = DialogResult.None;
                            _restartAmPmBox.Focus();
                            return;
                        }
                    }
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Width = 100,
                    Height = 32,
                    Margin = new Padding(8, 6, 0, 6)
                };

                var btnReset = new Button
                {
                    Text = "Reset to Defaults",
                    Width = 140,
                    Height = 32,
                    Margin = new Padding(8, 6, 0, 6)
                };
                btnReset.Click += (_, __) => ResetToDefaults();

                AcceptButton = btnSave;
                CancelButton = btnCancel;

                buttons.Controls.Add(btnSave);
                buttons.Controls.Add(btnCancel);
                buttons.Controls.Add(btnReset);
            }

            private void ResetToDefaults()
            {
                _exeBox.Text = DefaultServerExecutable;
                _argsBox.Text = string.Empty;

                _dataPathBox.Text = string.Empty;

                _autoSaveChk.Checked = false;
                _autoSaveMinutesBox.Text = "15";

                _autoBackupChk.Checked = false;
                _autoBackupMinutesBox.Text = "60";

                _autoRestartChk.Checked = false;
                _restartHourBox.SelectedItem = "6";
                _restartMinuteBox.SelectedItem = "00";
                _restartAmPmBox.SelectedItem = "AM";
            }

            private void BrowseForExe()
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Select Vintage Story Server Executable",
                    Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                    CheckFileExists = true,
                    CheckPathExists = true,
                    RestoreDirectory = true
                };

                var expanded = Environment.ExpandEnvironmentVariables(_exeBox.Text.Replace('/', '\\'));
                var dir = Path.GetDirectoryName(expanded);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    ofd.InitialDirectory = dir;

                if (ofd.ShowDialog(this) == DialogResult.OK)
                    _exeBox.Text = ofd.FileName;
            }

            private static void BrowseForFolder(TextBox target)
            {
                using var fbd = new FolderBrowserDialog
                {
                    Description = "Select VintageStoryServerData folder",
                    UseDescriptionForTitle = true
                };

                if (Directory.Exists(target.Text))
                    fbd.SelectedPath = target.Text;

                if (fbd.ShowDialog() == DialogResult.OK)
                    target.Text = fbd.SelectedPath;
            }

            private static void Minutes_KeyPressNumbersOnly(object? sender, KeyPressEventArgs e)
            {
                if (char.IsControl(e.KeyChar)) return;
                if (!char.IsDigit(e.KeyChar)) e.Handled = true;
            }

            private static bool IsValidMinutes(string? text)
            {
                if (!int.TryParse(text, out var v)) return false;
                return v >= 1 && v <= 999;
            }

            private static int ParseMinutesOrDefault(string? text, int fallback)
            {
                if (!int.TryParse(text, out var v)) return fallback;
                return (v >= 1 && v <= 999) ? v : fallback;
            }

            private static int ParseHour12OrDefault(string? text, int fallback)
            {
                if (!int.TryParse(text, out var v)) return fallback;
                return (v >= 1 && v <= 12) ? v : fallback;
            }

            private static int ParseMinuteOrDefault(string? text, int fallback)
            {
                if (!int.TryParse(text, out var v)) return fallback;
                return (v >= 0 && v <= 59) ? v : fallback;
            }

            private static string NormalizeAmPm(string? value)
            {
                if (string.Equals(value, "PM", StringComparison.OrdinalIgnoreCase)) return "PM";
                return "AM";
            }

            private static string ClampTextMinutes(int value, int fallback)
            {
                var v = (value >= 1 && value <= 999) ? value : fallback;
                return v.ToString();
            }

            private static string ClampMinuteText(int value, int fallback)
            {
                var v = (value >= 0 && value <= 59) ? value : fallback;
                return v.ToString("00");
            }

            private static string ClampHour12Text(int value, int fallback)
            {
                var v = (value >= 1 && value <= 12) ? value : fallback;
                return v.ToString();
            }
        }

        // ==========================
        // MODS WINDOW
        // ==========================
        private sealed class ModsForm : Form
        {
            private readonly ListBox _list;
            private readonly string _modsPath;

            public ModsForm(string modsPath)
            {
                _modsPath = modsPath;

                Text = "Mods";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = false;
                MaximizeBox = true;
                ShowInTaskbar = false;

                MinimumSize = new Size(520, 360);
                ClientSize = new Size(720, 420);

                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
                Controls.Add(root);

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 2
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.Controls.Add(layout);

                var label = new Label
                {
                    Text = "Installed mods (files in Mods folder):",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                layout.Controls.Add(label, 0, 0);
                layout.SetColumnSpan(label, 2);

                _list = new ListBox { Dock = DockStyle.Fill };
                layout.Controls.Add(_list, 0, 1);

                var right = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false
                };
                layout.Controls.Add(right, 1, 1);

                var btnOpenFolder = new Button
                {
                    Text = "Open Folder",
                    Width = 150,
                    Height = 34,
                    Margin = new Padding(6)
                };
                btnOpenFolder.Click += (_, __) => OpenFolder(_modsPath);
                right.Controls.Add(btnOpenFolder);

                var btnRefresh = new Button
                {
                    Text = "Refresh",
                    Width = 150,
                    Height = 34,
                    Margin = new Padding(6)
                };
                btnRefresh.Click += (_, __) => LoadMods();
                right.Controls.Add(btnRefresh);

                LoadMods();
            }

            private void LoadMods()
            {
                _list.Items.Clear();

                try
                {
                    if (!Directory.Exists(_modsPath))
                    {
                        _list.Items.Add("Mods folder not found:");
                        _list.Items.Add(_modsPath);
                        return;
                    }

                    var files = Directory.GetFiles(_modsPath);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                    foreach (var f in files)
                        _list.Items.Add(Path.GetFileName(f));

                    if (files.Length == 0)
                        _list.Items.Add("(No files found)");
                }
                catch (Exception ex)
                {
                    _list.Items.Add("Failed to load mods: " + ex.Message);
                }
            }

            private static void OpenFolder(string path)
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        MessageBox.Show($"Folder does not exist:\n{path}", "Open Folder",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to open folder: " + ex.Message, "Open Folder",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}

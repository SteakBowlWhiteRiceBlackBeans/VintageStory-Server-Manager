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
using System.Linq;


namespace Drewski_s_Server_Manager
{
    public partial class Form1 : Form
    {

        private int S(int px) => (int)Math.Round(px * (DeviceDpi / 96.0));
        private Size S(Size sz) => new Size(S(sz.Width), S(sz.Height));
        private Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

        private RichTextBox _console = null!;
        private Panel _titleBar = null!;
        private Label _titleLabel = null!;
        private Button _btnMin = null!;
        private Button _btnMax = null!;
        private Button _btnClose = null!;
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
        private Button _btnBackupFolder = null!;
        private Button _btnModPage = null!;
        private TextBox _commandBox = null!;
        private Button _btnSendCmd = null!;
        private Process? _serverProcess;
        private volatile bool _stopRequested;
        private const string DefaultServerExecutable = "%appdata%/Vintagestory/VintagestoryServer.exe";
        private string _serverExeSetting = DefaultServerExecutable;
        private string _launchArgsSetting = string.Empty;
        private string _serverDataPathSetting = string.Empty;
        private bool _autoSaveEnabledSetting;
        private int _autoSaveMinutesSetting = 15;
        private bool _autoBackupEnabledSetting;
        private int _autoBackupMinutesSetting = 60;
        private int _maxBackupFileCountSetting;
        private double _maxBackupFolderSizeGbSetting;
        private bool _autoRestartEnabledSetting;
        private int _autoRestartHour12Setting = 6;
        private int _autoRestartMinuteSetting = 0;
        private string _autoRestartAmPmSetting = "AM";
        private const int WM_NCHITTEST = 0x84;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int ResizeBorder = 8;
        private readonly Color _resizeBorderColor = Color.FromArgb(45, 45, 45);
        private const int CustomBorderThickness = 1;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int MaxConsoleLines = 3000;
        private const int ConsoleTrimHysteresis = 150;
        private readonly Color _consoleInfoColor = Color.Gainsboro;
        private readonly Color _consoleWarnColor = Color.FromArgb(240, 200, 70);
        private readonly Color _consoleErrorColor = Color.FromArgb(235, 90, 90);
        private System.Windows.Forms.Timer? _spamTimer;
        private long _spamCounter;

        private sealed class AppSettings
        {
            public string ServerExecutable { get; set; } = DefaultServerExecutable;
            public string LaunchArguments { get; set; } = "";

            public string ServerDataPath { get; set; } = "";

            public bool AutoSaveEnabled { get; set; } = false;
            public int AutoSaveMinutes { get; set; } = 15;

            public bool AutoBackupEnabled { get; set; } = false;
            public int AutoBackupMinutes { get; set; } = 60;

            public int MaxBackupFileCount { get; set; } = 0; // 0 = unlimited
            public double MaxBackupFolderSizeGb { get; set; } = 0; // 0 = unlimited

            public bool AutoRestartEnabled { get; set; } = false;
            public int AutoRestartHour12 { get; set; } = 6;
            public int AutoRestartMinute { get; set; } = 0;
            public string AutoRestartAmPm { get; set; } = "AM";

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

        private readonly HashSet<int> _restartAnnouncementsSent = new();

        private const string RestartAnnounceCommand = "/say";

        private readonly Color _appBg = Color.FromArgb(24, 24, 24);
        private readonly Color _titleBg = Color.FromArgb(40, 40, 40);

        private readonly Color _panelBg = Color.FromArgb(32, 32, 32);
        private readonly Color _textColor = Color.FromArgb(30, 30, 30);

        private const int CornerRadius = 18;

        public Form1()
        {
            InitializeComponent();
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildUi();
            LoadSettings();

            _playerPollTimer.Interval = 15_000;
            _playerPollTimer.Tick += async (_, __) => await PollPlayerCountAsync();

            _resourceTimer.Interval = 1000; // 1 second
            _resourceTimer.Tick += (_, __) => UpdateResourceUsage();

            _autoSaveTimer.Tick += (_, __) => OnAutoSaveTick();
            _autoBackupTimer.Tick += (_, __) => OnAutoBackupTick();

            _autoRestartTimer.Interval = 20_000;
            _autoRestartTimer.Tick += async (_, __) => await CheckAutoRestartAsync();

            ApplyAutomationScheduling(serverRunning: false);
        }

        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;






        private void BuildUi()
        {
            Text = "Drewski's Server Manager"; // This is for the OS, not title bar
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = S(new Size(1200, 740));
            MinimumSize = S(new Size(1000, 740));
            BackColor = _appBg;
            FormBorderStyle = FormBorderStyle.None;
            Padding = S(new Padding(8));

            Shown += (_, __) => ApplyRoundedCornersIfNeeded();
            SizeChanged += (_, __) => ApplyRoundedCornersIfNeeded();

            FormClosing += async (_, __) =>
            {
                SaveSettings();
                await StopServerAsync(forceKill: true);
            };

            var root = new Panel { Dock = DockStyle.Fill, BackColor = _appBg };
            Controls.Add(root);

            _titleBar = new Panel { Dock = DockStyle.Top, Height = S(44), BackColor = _titleBg };
            root.Controls.Add(_titleBar);
            _titleBar.MouseDown += TitleBar_MouseDown;

            var raw = FileVersionInfo.GetVersionInfo(Application.ExecutablePath).ProductVersion ?? "0.0.0";
            var clean = raw.Split('+')[0]; // drops the + and commit hash after it


            _titleLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Left,
                Width = S(360),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = S(new Padding(14, 0, 0, 0)),
                Text = $"Drewski's Server Manager v{clean}", // Title bar text
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular)
            };
            _titleLabel.MouseDown += TitleBar_MouseDown;
            _titleBar.Controls.Add(_titleLabel);

            var btnPanel = new Panel { Dock = DockStyle.Right, Width = S(160), BackColor = _titleBg };
            _titleBar.Controls.Add(btnPanel);

            _btnClose = MakeTitleButton("X");
            _btnClose.Font = new Font("Segoe UI", 10.0f, FontStyle.Regular);
            _btnClose.Click += (_, __) => Close();

            _btnMax = MakeTitleButton("□");
            _btnMax.Font = new Font("Segoe UI", 14.0f, FontStyle.Regular);
            _btnMax.Width = S(60);
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
                Padding = S(new Padding(16, 32, 12, 16))
            };
            root.Controls.Add(content);


            // RIGHT PANEL — fixed width
            _rightArea = new Panel
            {
                Dock = DockStyle.Right,
                Width = S(340), // tweak this to taste
                BackColor = _appBg,
                Padding = S(new Padding(18, 8, 8, 8)),
                AutoScroll = true
            };

            // LEFT PANEL — console expands automatically
            var consoleHost = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = S(new Padding(14)),
                BackColor = Color.Black
            };

            content.Controls.Add(consoleHost);
            content.Controls.Add(_rightArea);

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

            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.Control && e.Shift && e.KeyCode == Keys.S)
                {
                    e.SuppressKeyPress = true;
                    //StartConsoleSpam(totalLines: 3500, linesPerTick: 200, intervalMs: 25);
                }
            };
        }


        private void BuildRightPanel(Panel host)
        {
            host.Controls.Clear();

            // Bottom command panel
            var cmdPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = S(44),
                BackColor = _appBg,
                Padding = S(new Padding(0, 8, 0, 0))
            };
            host.Controls.Add(cmdPanel);

            // Top card
            var card = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(620),
                BackColor = _panelBg,
                Padding = S(new Padding(16))
            };
            host.Controls.Add(card);

            // Send button
            _btnSendCmd = new Button
            {
                Text = "Send",
                Dock = DockStyle.Right,
                Width = S(90),
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
                Width = S(8),
                BackColor = _appBg
            };

            var cmdBoxBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Padding = S(new Padding(1))
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
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendCommandFromUi();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                }
            };
            _commandBox.Multiline = true;
            _commandBox.AcceptsReturn = false;
            _commandBox.ScrollBars = ScrollBars.None;
            _commandBox.WordWrap = false;

            cmdBoxBorder.Controls.Add(_commandBox);

            cmdPanel.Controls.Add(cmdBoxBorder);
            cmdPanel.Controls.Add(spacer);
            cmdPanel.Controls.Add(_btnSendCmd);

            var buttonsHost = new Panel
            {
                Dock = DockStyle.Top,
                BackColor = _panelBg
            };
            card.Controls.Add(buttonsHost);

            // Create buttons
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
                EnforceBackupRetentionBeforeCreatingNew();
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                SendCommand($"/genbackup {stamp}", echo: true);
            };

            _btnBackupFolder = MakeActionButton("Backup Folder");
            _btnBackupFolder.Click += (_, __) => OpenBackupsFolder();

            _btnMods = MakeActionButton("Mods");
            _btnMods.Click += (_, __) => ShowModsDialog();

            _btnSettings = MakeActionButton("Settings");
            _btnSettings.Click += (_, __) => ShowSettingsDialog();

            _btnModPage = MakeActionButton("Mod Page");
            _btnModPage.Click += (_, __) =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://mods.vintagestory.at/show/mod/41122",
                    UseShellExecute = true
                });
            };

            // These need to be in order
            var buttons = new Button[]
            {
    _btnStart,
    _btnStop,
    _btnRestart,
    _btnSave,
    _btnBackup,
    _btnBackupFolder,
    _btnMods,
    _btnSettings,
    _btnModPage
            };

            const int btnH = 44;
            const int gapY = 10;

            buttonsHost.Height = (buttons.Length * btnH) + ((buttons.Length - 1) * gapY);

            int y = 0;
            foreach (var b in buttons)
            {
                b.Left = 0;
                b.Top = y;
                b.Width = buttonsHost.ClientSize.Width;
                b.Height = btnH;
                b.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                buttonsHost.Controls.Add(b);
                y += btnH + gapY;
            }


            // Player count row
            var playerRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(28),
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
                Padding = S(new Padding(18, 0, 0, 0))
            };
            playerRow.Controls.Add(_playerCountLabel);

            // Resource usage row (under Status)
            var resourceRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(26),
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
                Padding = S(new Padding(18, 0, 0, 0))
            };
            resourceRow.Controls.Add(_resourceLabel);

            // Status row
            var statusRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(38),
                BackColor = _panelBg
            };
            card.Controls.Add(statusRow);

            _statusDot = new Panel
            {
                Width = S(12),
                Height = S(12),
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
                Width = S(360),
                Height = S(22),
                Text = "Status: Stopped",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular)
            };
            statusRow.Controls.Add(_statusLabel);

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = S(28),
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

                int? maxBackupFileCount = null;
                double? maxBackupFolderSizeGb = null;

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

                    maxBackupFileCount = TryGetInt(root, "MaxBackupFileCount") ?? TryGetInt(root, "maxBackupFileCount");

                    maxBackupFolderSizeGb = TryGetDouble(root, "MaxBackupFolderSizeGb") ?? TryGetDouble(root, "maxBackupFolderSizeGb");


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

                _maxBackupFileCountSetting = ClampMaxBackupCount(maxBackupFileCount ?? _settings.MaxBackupFileCount, 0);
                _maxBackupFolderSizeGbSetting = ClampMaxBackupSizeGb(maxBackupFolderSizeGb ?? _settings.MaxBackupFolderSizeGb, 0);

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
                try { CrashLogger.SetServerDataPath(_serverDataPathSetting); } catch { }
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

        private static double? TryGetDouble(JsonElement obj, string propName)
        {
            if (!obj.TryGetProperty(propName, out var v)) return null;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;

            return null;
        }

        private static int ClampMaxBackupCount(int value, int fallback)
        {
            // 0 = unlimited
            if (value < 0) return fallback;
            if (value > 100000) return 100000;
            return value;
        }

        private static double ClampMaxBackupSizeGb(double value, double fallback)
        {
            // 0 = unlimited
            if (value < 0) return fallback;
            if (value > 100000) return 100000;
            return value;
        }



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

                _settings.MaxBackupFileCount = ClampMaxBackupCount(_maxBackupFileCountSetting, 0);
                _settings.MaxBackupFolderSizeGb = ClampMaxBackupSizeGb(_maxBackupFolderSizeGbSetting, 0);

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
                _maxBackupFileCountSetting,
                _maxBackupFolderSizeGbSetting,
                _autoRestartEnabledSetting,
                _autoRestartHour12Setting,
                _autoRestartMinuteSetting,
                _autoRestartAmPmSetting
            );

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            _serverExeSetting = dlg.ServerExecutable;
            _launchArgsSetting = dlg.LaunchArguments;

            _serverDataPathSetting = dlg.ServerDataPath;

            _autoSaveEnabledSetting = dlg.AutoSaveEnabled;
            _autoSaveMinutesSetting = dlg.AutoSaveMinutes;

            _autoBackupEnabledSetting = dlg.AutoBackupEnabled;
            _autoBackupMinutesSetting = dlg.AutoBackupMinutes;

            _maxBackupFileCountSetting = dlg.MaxBackupFileCount;
            _maxBackupFolderSizeGbSetting = dlg.MaxBackupFolderSizeGb;

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
                Width = S(50),
                Height = S(44),
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
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(230, 230, 230),
                Font = new Font("Segoe UI", 10.0f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false
            };

            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60);
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(75, 75, 75);

            return btn;
        }


        private void AppendConsoleLine(string line)
        {
            if (_console == null || _console.IsDisposed) return;

            bool wasAtBottom = IsConsoleAtBottom();

            var c = _consoleInfoColor;

            if (line.IndexOf("[Server Error]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Critical Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("[err]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                c = _consoleErrorColor;
            }
            else if (line.IndexOf("[Server Warning]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                c = _consoleWarnColor;
            }

            _console.SuspendLayout();
            try
            {
                _console.SelectionStart = _console.TextLength;
                _console.SelectionLength = 0;
                _console.SelectionColor = c;
                _console.AppendText(line + Environment.NewLine);
                _console.SelectionColor = _consoleInfoColor;

                // Robust trim that avoids RichEdit “ding” behavior
                TrimConsoleToMaxLines_NoBeep();

                if (wasAtBottom)
                {
                    _console.SelectionStart = _console.TextLength;
                    _console.ScrollToCaret();
                }
            }
            catch
            {
                // keep resilient
            }
            finally
            {
                _console.ResumeLayout();
            }
        }


        private bool IsConsoleAtBottom()
        {
            if (_console == null || _console.IsDisposed) return true;

            // Char index at bottom-left of the visible area
            int lastVisibleChar = _console.GetCharIndexFromPosition(new Point(1, _console.ClientRectangle.Bottom - 1));
            return lastVisibleChar >= _console.TextLength - 2;
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

        // Detect manual --datapath usage in Launch Arguments
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


        private void OpenBackupsFolder()
        {
            try
            {

                if (string.IsNullOrWhiteSpace(_serverDataPathSetting))
                {
                    MessageBox.Show(this,
                        "Set the VintageStoryServerData folder in Settings first.",
                        "Backup Folder",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                var backupsDir = GetBackupsFolderPath();

                if (string.IsNullOrWhiteSpace(backupsDir))
                {
                    MessageBox.Show(this, "Backups folder path is empty.", "Backup Folder",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (!Directory.Exists(backupsDir))
                    Directory.CreateDirectory(backupsDir);

                AppendConsoleLine($"[manager] Opening backups folder: {backupsDir}");

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{backupsDir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to open backups folder: " + ex.Message, "Backup Folder",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private string GetBackupsFolderPath()
        {
            string dataRoot;

            if (!string.IsNullOrWhiteSpace(_serverDataPathSetting))
                dataRoot = ResolvePath(_serverDataPathSetting);
            else
                dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VintagestoryData");

            return Path.Combine(dataRoot, "Backups");
        }




        // AUTOMATION IMPLEMENTATION
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
                EnforceBackupRetentionBeforeCreatingNew();
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                SendCommand($"/genbackup {stamp}", echo: true);
            }
            finally
            {
                if (IsServerRunning() && _autoBackupEnabledSetting)
                    _autoBackupTimer.Start();
            }
        }

        private void EnforceBackupRetentionBeforeCreatingNew()
        {
            int maxFiles = ClampMaxBackupCount(_maxBackupFileCountSetting, 0);

            long maxBytes = 0;
            if (_maxBackupFolderSizeGbSetting > 0)
                maxBytes = (long)(_maxBackupFolderSizeGbSetting * 1024d * 1024d * 1024d);

            if (maxFiles == 0 && maxBytes == 0) return;

            var backupsDir = GetBackupsFolderPath();
            if (string.IsNullOrWhiteSpace(backupsDir) || !Directory.Exists(backupsDir))
                return;

            try
            {
                var di = new DirectoryInfo(backupsDir);

                var files = di.GetFiles()
                    .OrderBy(f => f.CreationTimeUtc)
                    .ThenBy(f => f.LastWriteTimeUtc)
                    .ToList();

                // safety net, never delete da newest
                if (files.Count <= 1) return;

                var newest = files
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ThenByDescending(f => f.LastWriteTimeUtc)
                    .First();

                if (maxFiles > 0)
                {
                    while (files.Count >= maxFiles && files.Count > 1)
                    {
                        var oldest = files.First();

                        // never delete newest
                        if (string.Equals(oldest.FullName, newest.FullName, StringComparison.OrdinalIgnoreCase))
                            break;

                        if (!TryDeleteFile(oldest))
                            break;

                        files.RemoveAt(0);
                    }
                }
                if (maxBytes > 0 && files.Count > 1)
                {
                    long total = files.Sum(f => f.Length);

                    while (total > maxBytes && files.Count > 1)
                    {
                        var oldest = files.First();

                        // never delete newest
                        if (string.Equals(oldest.FullName, newest.FullName, StringComparison.OrdinalIgnoreCase))
                            break;

                        long len = oldest.Length;

                        if (!TryDeleteFile(oldest))
                            break;

                        files.RemoveAt(0);
                        total -= len;
                    }
                }
            }
            catch (Exception ex)
            {
                AppendConsoleLine("[manager] Backup retention check failed: " + ex.Message);
            }
        }

        private static bool TryDeleteFile(FileInfo f)
        {
            try
            {
                f.Refresh();
                if (!f.Exists) return true;

                if (f.IsReadOnly) f.IsReadOnly = false;

                f.Delete();
                return true;
            }
            catch
            {
                return false;
            }
        }


        private async Task CheckAutoRestartAsync()
        {
            if (!IsServerRunning()) return;
            if (!_autoRestartEnabledSetting) return;
            if (_autoRestartInProgress) return;

            var now = DateTime.Now;
            var scheduled = BuildScheduledRestartTime(now.Date);

            if (_restartAnnouncementsSent.Count > 0 && _lastAutoRestartDate.Date != now.Date)
            {
                _restartAnnouncementsSent.Clear();
            }

            var windowStart = scheduled.AddMinutes(-15);

            if (now >= windowStart && now <= scheduled)
            {
                var minutesRemaining = (int)Math.Ceiling((scheduled - now).TotalMinutes);
                if (minutesRemaining < 0) minutesRemaining = 0;

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

                        SendCommand($"{RestartAnnounceCommand} {msg}", echo: true);
                    }
                }
            }

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

            await Task.Delay(5000);

            AppendConsoleLine("[manager] Auto Restart: restarting server...");
            await StopServerAsync(forceKill: false);
            await StartServerAsync();
        }


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

        // SETTINGS WINDOW
        private sealed class SettingsForm : Form
        {

            // DPI scaling helpers
            private int S(int px) => (int)Math.Round(px * (DeviceDpi / 96.0));
            private Size S(Size sz) => new Size(S(sz.Width), S(sz.Height));
            private Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

            private readonly TextBox _exeBox;
            private readonly TextBox _argsBox;

            private readonly TextBox _dataPathBox;

            private readonly CheckBox _autoSaveChk;
            private readonly TextBox _autoSaveMinutesBox;

            private readonly CheckBox _autoBackupChk;
            private readonly TextBox _autoBackupMinutesBox;

            private readonly TextBox _maxBackupCountBox;
            private readonly TextBox _maxBackupSizeGbBox;

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

            public int MaxBackupFileCount => ParseIntOrDefault(_maxBackupCountBox.Text, 0);
            public double MaxBackupFolderSizeGb => ParseDoubleOrDefault(_maxBackupSizeGbBox.Text, 0);

            public bool AutoRestartEnabled => _autoRestartChk.Checked;
            public int AutoRestartHour12 => ParseHour12OrDefault(_restartHourBox.SelectedItem?.ToString(), 6);
            public int AutoRestartMinute => ParseMinuteOrDefault(_restartMinuteBox.SelectedItem?.ToString(), 0);
            public string AutoRestartAmPm => NormalizeAmPm(_restartAmPmBox.SelectedItem?.ToString());

            public SettingsForm(
                string exeValue,
                string argsValue,
                string dataPathValue,
                bool autoSaveEnabled,
                int autoSaveMinutes,
                bool autoBackupEnabled,
                int autoBackupMinutes,
                int maxBackupFileCount,
                double maxBackupFolderSizeGb,
                bool autoRestartEnabled,
                int autoRestartHour12,
                int autoRestartMinute,
                string autoRestartAmPm
            )
            {

                AutoScaleMode = AutoScaleMode.Dpi;
                Text = "Settings";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = false;
                MaximizeBox = true;
                ShowInTaskbar = false;

                MinimumSize = S(new Size(700, 580));
                ClientSize = S(new Size(820, 580));

                var root = new Panel { Dock = DockStyle.Fill, Padding = S(new Padding(14)) };
                Controls.Add(root);

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 20
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.Controls.Add(layout);

                // Row styles
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(14)));

                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(54)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(14)));

                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(14)));

                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));

                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));

                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));

                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(44)));

                // Server Executable
                layout.Controls.Add(new Label
                {
                    Text = "Server Executable",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.BottomLeft
                }, 0, 0);

                var exeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
                exeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                exeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(110)));
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
                    Height = S(26),
                    Text = "Selecting a new server data folder location will automatically include the argument --datapath \"server data folder\"",
                    ForeColor = Color.FromArgb(90, 90, 90),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                    Padding = S(new Padding(2, 6, 2, 0))
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
                dataRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(110)));
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

                // Automation header
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

                // Max backup file count row
                var maxCountRow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = false
                };
                layout.Controls.Add(maxCountRow, 0, 12);

                maxCountRow.Controls.Add(new Label
                {
                    Text = "Max backup files     0 = unlimited",
                    AutoSize = true,
                    Margin = new Padding(0, 8, 12, 0)
                });

                _maxBackupCountBox = new TextBox
                {
                    Width = S(90),
                    Text = Math.Max(0, maxBackupFileCount).ToString(),
                    Margin = new Padding(0, 5, 0, 0)
                };
                _maxBackupCountBox.KeyPress += NumbersOnly_KeyPress;
                maxCountRow.Controls.Add(_maxBackupCountBox);

                // Max backup folder size row
                var maxSizeRow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = false
                };
                layout.Controls.Add(maxSizeRow, 0, 13);

                maxSizeRow.Controls.Add(new Label
                {
                    Text = "Max backup folder size (GB)  0 = unlimited",
                    AutoSize = true,
                    Margin = new Padding(0, 8, 12, 0)
                });

                _maxBackupSizeGbBox = new TextBox
                {
                    Width = S(90),
                    Text = (maxBackupFolderSizeGb < 0 ? 0 : maxBackupFolderSizeGb).ToString("0.##"),
                    Margin = new Padding(0, 5, 0, 0)
                };
                _maxBackupSizeGbBox.KeyPress += DecimalNumbersOnly_KeyPress;
                maxSizeRow.Controls.Add(_maxBackupSizeGbBox);

                // Auto Restart row
                var autoRestartRow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = false
                };
                layout.Controls.Add(autoRestartRow, 0, 14);

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

                _restartNote = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "Reboot announcement will be sent in chat 15 minutes prior and every 3 minutes after",
                    ForeColor = Color.FromArgb(90, 90, 90),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                    Padding = S(new Padding(2, 2, 2, 0)),
                    Visible = autoRestartEnabled
                };
                layout.Controls.Add(_restartNote, 0, 15);

                // Enable/disable dependent inputs
                _autoSaveChk.CheckedChanged += (_, __) => _autoSaveMinutesBox.Enabled = _autoSaveChk.Checked;

                void ApplyBackupFieldEnable()
                {
                    bool enabled = _autoBackupChk.Checked;
                    _autoBackupMinutesBox.Enabled = enabled;
                    _maxBackupCountBox.Enabled = enabled;
                    _maxBackupSizeGbBox.Enabled = enabled;
                }
                _autoBackupChk.CheckedChanged += (_, __) => ApplyBackupFieldEnable();

                _autoRestartChk.CheckedChanged += (_, __) =>
                {
                    _restartHourBox.Enabled = _autoRestartChk.Checked;
                    _restartMinuteBox.Enabled = _autoRestartChk.Checked;
                    _restartAmPmBox.Enabled = _autoRestartChk.Checked;
                    _restartNote.Visible = _autoRestartChk.Checked;
                };

                _autoSaveMinutesBox.Enabled = _autoSaveChk.Checked;

                _restartHourBox.Enabled = _autoRestartChk.Checked;
                _restartMinuteBox.Enabled = _autoRestartChk.Checked;
                _restartAmPmBox.Enabled = _autoRestartChk.Checked;

                ApplyBackupFieldEnable();

                // Buttons row
                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = S(new Padding(0)),
                    WrapContents = false
                };
                layout.Controls.Add(buttons, 0, 17);

                var btnSave = new Button
                {
                    Text = "Save",
                    DialogResult = DialogResult.OK,
                    Width = 100,
                    Height = S(32),
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

                    if (!int.TryParse(_maxBackupCountBox.Text, out var count) || count < 0)
                    {
                        MessageBox.Show(this,
                            "Max backups (files) must be 0 (unlimited) or a positive number.",
                            "Invalid Value",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);

                        DialogResult = DialogResult.None;
                        _maxBackupCountBox.Focus();
                        return;
                    }

                    if (!double.TryParse(_maxBackupSizeGbBox.Text, out var gb) || gb < 0)
                    {
                        MessageBox.Show(this,
                            "Max backup folder size must be 0 (unlimited) or a positive number.",
                            "Invalid Value",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);

                        DialogResult = DialogResult.None;
                        _maxBackupSizeGbBox.Focus();
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
                    Height = S(32),
                    Margin = new Padding(8, 6, 0, 6)
                };

                var btnReset = new Button
                {
                    Text = "Reset to Defaults",
                    Width = 140,
                    Height = S(32),
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

                _maxBackupCountBox.Text = "0";
                _maxBackupSizeGbBox.Text = "0";

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

            private static void NumbersOnly_KeyPress(object? sender, KeyPressEventArgs e)
            {
                if (char.IsControl(e.KeyChar)) return;
                if (!char.IsDigit(e.KeyChar)) e.Handled = true;
            }

            private static void DecimalNumbersOnly_KeyPress(object? sender, KeyPressEventArgs e)
            {
                if (char.IsControl(e.KeyChar)) return;

                if (sender is not TextBox tb) { e.Handled = true; return; }

                if (char.IsDigit(e.KeyChar)) return;

                if (e.KeyChar == '.' && !tb.Text.Contains('.')) return;

                e.Handled = true;
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

            private static int ParseIntOrDefault(string? text, int fallback)
            {
                if (!int.TryParse(text, out var v)) return fallback;
                return v < 0 ? fallback : v;
            }

            private static double ParseDoubleOrDefault(string? text, double fallback)
            {
                if (!double.TryParse(text, out var v)) return fallback;
                return v < 0 ? fallback : v;
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

        // MODS WINDOW
        private sealed class ModsForm : Form
        {

            // DPI scaling helpers
            private int S(int px) => (int)Math.Round(px * (DeviceDpi / 96.0));
            private Size S(Size sz) => new Size(S(sz.Width), S(sz.Height));
            private Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

            private readonly ListBox _list;
            private readonly string _modsPath;

            public ModsForm(string modsPath)
            {

                AutoScaleMode = AutoScaleMode.Dpi;
                _modsPath = modsPath;

                Text = "Mods";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = false;
                MaximizeBox = true;
                ShowInTaskbar = false;

                MinimumSize = S(new Size(520, 360));
                ClientSize = S(new Size(720, 420));

                var root = new Panel { Dock = DockStyle.Fill, Padding = S(new Padding(12)) };
                Controls.Add(root);

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 2
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(170)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));
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

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // Snap-to-top maximize when finishing a move/resize operation
            if (m.Msg == WM_EXITSIZEMOVE && WindowState == FormWindowState.Normal)
            {
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;

                // If the window ended up at/near the top of the working area, maximize it
                if (Top <= wa.Top + 2)
                {
                    WindowState = FormWindowState.Maximized;
                    ApplyRoundedCornersIfNeeded();
                }

                // Do not return; not required, but safe to continue
            }

            // Borderless resize hit-test
            if (m.Msg != WM_NCHITTEST || WindowState != FormWindowState.Normal)
                return;

            Point pt = PointToClient(Cursor.Position);

            bool left = pt.X <= ResizeBorder;
            bool right = pt.X >= ClientSize.Width - ResizeBorder;
            bool top = pt.Y <= ResizeBorder;
            bool bottom = pt.Y >= ClientSize.Height - ResizeBorder;

            if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
            if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
            if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
            if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }

            if (left) { m.Result = (IntPtr)HTLEFT; return; }
            if (right) { m.Result = (IntPtr)HTRIGHT; return; }
            if (top) { m.Result = (IntPtr)HTTOP; return; }
            if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }
        }


        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (WindowState == FormWindowState.Maximized)
                return;

            using var pen = new Pen(_resizeBorderColor, CustomBorderThickness);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }


        //private void StartConsoleSpam(int totalLines = 3500, int linesPerTick = 200, int intervalMs = 25)
        //{
        //    StopConsoleSpam();

        //    long target = _spamCounter + totalLines;

        //    _spamTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, intervalMs) };
        //    _spamTimer.Tick += (_, __) =>
        //    {
        //        int remaining = (int)Math.Min(linesPerTick, Math.Max(0, target - _spamCounter));
        //        for (int i = 0; i < remaining; i++)
        //        {
        //            _spamCounter++;
        //            AppendConsoleLine($"[SPAM] {_spamCounter} {DateTime.Now:HH:mm:ss.fff} Spam line");
        //        }

        //        if (_spamCounter >= target)
        //            StopConsoleSpam();
        //    };
        //    _spamTimer.Start();

        //    AppendConsoleLine($"[SPAM] started, will add {totalLines} lines");
        //}

        //private void StopConsoleSpam()
        //{
        //    if (_spamTimer == null) return;
        //    _spamTimer.Stop();
        //    _spamTimer.Dispose();
        //    _spamTimer = null;
        //    AppendConsoleLine("[SPAM] stopped");
        //}
        

private static class Native
    {
        public const int WM_SETREDRAW = 0x000B;
        public const int EM_SETSEL = 0x00B1;
        public const int EM_REPLACESEL = 0x00C2;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        }

private void TrimConsoleToMaxLines_NoBeep()
        {
            if (_console == null || _console.IsDisposed || !_console.IsHandleCreated) return;

            int lineCount = _console.GetLineFromCharIndex(_console.TextLength) + 1;
            if (lineCount <= MaxConsoleLines) return;

            int removeLines = lineCount - MaxConsoleLines;
            int charIndex = _console.GetFirstCharIndexFromLine(removeLines);
            if (charIndex <= 0) return;

            Native.SendMessage(_console.Handle, Native.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                Native.SendMessage(_console.Handle, Native.EM_SETSEL, IntPtr.Zero, (IntPtr)charIndex);
                Native.SendMessage(_console.Handle, Native.EM_REPLACESEL, (IntPtr)1, "");
            }
            finally
            {
                Native.SendMessage(_console.Handle, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _console.Invalidate();
            }
        }



    }
}

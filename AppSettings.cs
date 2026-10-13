using System;
using System.IO;
using System.Text.Json;

namespace VintageStory_Server_Manager
{
    public class AppSettings
    {
        public string ServerExecutable { get; set; } = @"%appdata%\Vintagestory\VintagestoryServer.exe";
        public string LaunchArguments { get; set; } = "";
        public string DataFolder { get; set; } = @"%appdata%\VintagestoryData";

        public bool AutoSaveEnabled { get; set; } = false;
        public int AutoSaveMinutes { get; set; } = 15;

        public bool AutoBackupEnabled { get; set; } = false;
        public int AutoBackupMinutes { get; set; } = 60;

        public int MaxBackupFiles { get; set; } = 0;
        public int MaxBackupFolderSizeGB { get; set; } = 0;

        public bool AutoRestartEnabled { get; set; } = false;
        public int RestartHour { get; set; } = 3;
        public int RestartMinute { get; set; } = 0;
        public string RestartAmPm { get; set; } = "AM";
    }

    public static class AppConfig
    {
        private static readonly string ConfigDir = Environment.ExpandEnvironmentVariables(@"%appdata%\VintagestoryData\ModConfig");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "VSServerManager.json");

        public static AppSettings LoadOrCreate()
        {
            Directory.CreateDirectory(ConfigDir);

            if (!File.Exists(ConfigPath))
            {
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            Directory.CreateDirectory(ConfigDir);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, options));
        }
    }
}
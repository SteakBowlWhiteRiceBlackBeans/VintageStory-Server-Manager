using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace VintageStory_Server_Manager
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;

        public SettingsWindow()
        {
            InitializeComponent();

            for (int i = 1; i <= 12; i++)
                RestartHourCombo.Items.Add(i);
            for (int i = 0; i < 60; i++)
                RestartMinuteCombo.Items.Add(i.ToString("00"));
            RestartAmPmCombo.Items.Add("AM");
            RestartAmPmCombo.Items.Add("PM");

            _settings = AppConfig.LoadOrCreate();
            LoadIntoControls();
        }

        private void LoadIntoControls()
        {
            ServerExeTextBox.Text = _settings.ServerExecutable;
            LaunchArgsTextBox.Text = _settings.LaunchArguments;
            DataFolderTextBox.Text = _settings.DataFolder;

            AutoSaveCheckBox.IsChecked = _settings.AutoSaveEnabled;
            AutoSaveMinutesTextBox.Text = _settings.AutoSaveMinutes.ToString();
            AutoSaveMinutesTextBox.IsEnabled = _settings.AutoSaveEnabled;

            AutoBackupCheckBox.IsChecked = _settings.AutoBackupEnabled;
            AutoBackupMinutesTextBox.Text = _settings.AutoBackupMinutes.ToString();
            AutoBackupMinutesTextBox.IsEnabled = _settings.AutoBackupEnabled;

            MaxBackupFilesTextBox.Text = _settings.MaxBackupFiles.ToString();
            MaxBackupSizeTextBox.Text = _settings.MaxBackupFolderSizeGB.ToString();

            AutoRestartCheckBox.IsChecked = _settings.AutoRestartEnabled;
            RestartHourCombo.SelectedItem = _settings.RestartHour;
            RestartMinuteCombo.SelectedItem = _settings.RestartMinute.ToString("00");
            RestartAmPmCombo.SelectedItem = _settings.RestartAmPm;
            RestartHourCombo.IsEnabled = _settings.AutoRestartEnabled;
            RestartMinuteCombo.IsEnabled = _settings.AutoRestartEnabled;
            RestartAmPmCombo.IsEnabled = _settings.AutoRestartEnabled;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.ServerExecutable = ServerExeTextBox.Text.Trim();
            _settings.LaunchArguments = LaunchArgsTextBox.Text.Trim();
            _settings.DataFolder = DataFolderTextBox.Text.Trim();

            _settings.AutoSaveEnabled = AutoSaveCheckBox.IsChecked == true;
            _settings.AutoSaveMinutes = int.TryParse(AutoSaveMinutesTextBox.Text, out int saveMin) ? saveMin : 15;

            _settings.AutoBackupEnabled = AutoBackupCheckBox.IsChecked == true;
            _settings.AutoBackupMinutes = int.TryParse(AutoBackupMinutesTextBox.Text, out int backupMin) ? backupMin : 60;

            _settings.MaxBackupFiles = int.TryParse(MaxBackupFilesTextBox.Text, out int maxFiles) ? maxFiles : 0;
            _settings.MaxBackupFolderSizeGB = int.TryParse(MaxBackupSizeTextBox.Text, out int maxSize) ? maxSize : 0;

            _settings.AutoRestartEnabled = AutoRestartCheckBox.IsChecked == true;
            _settings.RestartHour = RestartHourCombo.SelectedItem is int h ? h : 3;
            _settings.RestartMinute = RestartMinuteCombo.SelectedItem is string m && int.TryParse(m, out int min) ? min : 0;
            _settings.RestartAmPm = RestartAmPmCombo.SelectedItem as string ?? "AM";

            AppConfig.Save(_settings);
            Close();
        }

        private void AutoSaveCheckBox_Checked(object sender, RoutedEventArgs e) => AutoSaveMinutesTextBox.IsEnabled = true;
        private void AutoSaveCheckBox_Unchecked(object sender, RoutedEventArgs e) => AutoSaveMinutesTextBox.IsEnabled = false;

        private void AutoBackupCheckBox_Checked(object sender, RoutedEventArgs e) => AutoBackupMinutesTextBox.IsEnabled = true;
        private void AutoBackupCheckBox_Unchecked(object sender, RoutedEventArgs e) => AutoBackupMinutesTextBox.IsEnabled = false;

        private void AutoRestartCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            RestartHourCombo.IsEnabled = true;
            RestartMinuteCombo.IsEnabled = true;
            RestartAmPmCombo.IsEnabled = true;
        }
        private void AutoRestartCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            RestartHourCombo.IsEnabled = false;
            RestartMinuteCombo.IsEnabled = false;
            RestartAmPmCombo.IsEnabled = false;
        }

        private void BrowseServerExe_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Vintage Story Server Executable",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = System.IO.Path.GetFileName(ServerExeTextBox.Text)
            };

            if (dialog.ShowDialog() == true)
                ServerExeTextBox.Text = dialog.FileName;
        }

        private void BrowseDataFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select VintageStoryServerData Folder",
                InitialDirectory = DataFolderTextBox.Text
            };

            if (dialog.ShowDialog() == true)
                DataFolderTextBox.Text = dialog.FolderName;
        }

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Reset all settings to defaults?",
                "Reset to Defaults",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            _settings = new AppSettings();
            AppConfig.Save(_settings);
            LoadIntoControls();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            _settings = AppConfig.LoadOrCreate();
            string dataPath = Environment.ExpandEnvironmentVariables(_settings.DataFolder);
            string configFile = System.IO.Path.Combine(dataPath, "serverconfig.json");

            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{configFile}\"",
                UseShellExecute = true
            });
        }


    }
}
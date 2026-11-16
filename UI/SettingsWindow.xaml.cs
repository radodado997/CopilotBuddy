using System;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using Styx.Helpers;
using Styx;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// Settings window for CopilotBuddy, similar to HB WoD settings
    /// </summary>
    public partial class SettingsWindow : MetroWindow
    {
        /// <summary>
        /// True while the ctor fills the password box, so the PasswordChanged handler
        /// does not write the value straight back into the settings.
        /// </summary>
        private bool _loadingReloginPassword;

        public SettingsWindow()
        {
            InitializeComponent();
            
            // Set DataContext to enable bindings to CharacterSettings and StyxSettings
            DataContext = new SettingsDataContext
            {
                CharSettings = CharacterSettings.Instance,
                StyxSettings = StyxSettings.Instance,
                Relogin = ReloginSettings.Instance
            };

            // PasswordBox has no bindable Password property, so it is filled here and
            // written back from the PasswordChanged handler.
            _loadingReloginPassword = true;
            try
            {
                pwdReloginPassword.Password = ReloginSettings.Instance.Password ?? "";
            }
            catch { /* ignore */ }
            _loadingReloginPassword = false;

            // Initialize log level ComboBox
            InitializeLogLevelComboBox();

            // Initialize language ComboBox
            InitializeLanguageComboBox();

            // Restore window position/size
            try
            {
                if (UISettings.Instance.SettingsWindowWidth > 0)
                    this.Width = UISettings.Instance.SettingsWindowWidth;
                if (UISettings.Instance.SettingsWindowHeight > 0)
                    this.Height = UISettings.Instance.SettingsWindowHeight;
                if (UISettings.Instance.SettingsWindowLocationX >= 0)
                    this.Left = UISettings.Instance.SettingsWindowLocationX;
                if (UISettings.Instance.SettingsWindowLocationY >= 0)
                    this.Top = UISettings.Instance.SettingsWindowLocationY;
                if (UISettings.Instance.SettingsWindowState != WindowState.Minimized)
                    this.WindowState = UISettings.Instance.SettingsWindowState;
            }
            catch { /* Ignore errors on first run */ }
        }

        private void InitializeLogLevelComboBox()
        {
            // Set selected item based on current LoggingLevel
            int logLevelIndex = (int)StyxSettings.Instance.LoggingLevel;
            if (logLevelIndex >= 0 && logLevelIndex < cmbLogLevel.Items.Count)
            {
                cmbLogLevel.SelectedIndex = logLevelIndex;
            }
        }

        private void InitializeLanguageComboBox()
        {
            // Set selected item based on current StyxSettings.Language.
            // HB 6.2.3 WoD pattern: set SelectedItem in ctor (IsLoaded=false), the
            // SelectionChanged handler ignores this initial set via its IsLoaded guard.
            string currentLang = StyxSettings.Instance.Language ?? "";
            foreach (var item in cmbLanguage.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag is string lang && lang == currentLang)
                {
                    cmbLanguage.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void cmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (cmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string lang)
            {
                StyxSettings.Instance.Language = lang;
                StyxSettings.Instance.Save();
                Styx.Localization.Globalization.ApplyLanguage(lang);
            }
        }

        private void cmbLogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbLogLevel.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Tag?.ToString(), out int level))
                {
                    StyxSettings.Instance.LoggingLevel = (LogLevel)level;
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Save window position/size
            try
            {
                if (this.WindowState == WindowState.Normal)
                {
                    UISettings.Instance.SettingsWindowWidth = (int)this.Width;
                    UISettings.Instance.SettingsWindowHeight = (int)this.Height;
                    UISettings.Instance.SettingsWindowLocationX = (int)this.Left;
                    UISettings.Instance.SettingsWindowLocationY = (int)this.Top;
                }
                UISettings.Instance.SettingsWindowState = this.WindowState;
                UISettings.Instance.Save();
            }
            catch { /* Ignore errors */ }
            
            base.OnClosing(e);
        }

        private void btnSaveAndClose_Click(object sender, RoutedEventArgs e)
        {
            // Save both settings. CharacterSettings only exists once a character has
            // been attached, and the window is reachable before that (Auto Relogin).
            CharacterSettings.Instance?.Save();
            StyxSettings.Instance.Save();

            // Auto Relogin settings are global (not per character) and include the
            // encrypted password, which never goes through a binding.
            ReloginSettings.Instance.Save();

            // The monitor may have been idle because the feature was off at startup;
            // enable it now instead of asking for a restart. (Start() is a no-op when
            // it is already running, and the monitor itself does nothing while the
            // feature is disabled.)
            if (ReloginSettings.Instance.Enabled)
            {
                try
                {
                    Styx.Logic.Relogin.ReloginManager.Instance.Start();

                    // Saving with the feature enabled is also how a pause caused by an
                    // intentional logout (inactivity detector) is cleared.
                    Styx.Logic.Relogin.ReloginManager.Instance.Resume();
                }
                catch (Exception ex)
                {
                    Styx.Helpers.Logging.WriteDebug("Auto Relogin: could not start the monitor: {0}", ex.Message);
                }
            }

            // Close the window
            Close();
        }

        private void pwdReloginPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loadingReloginPassword)
                return;

            ReloginSettings.Instance.Password = pwdReloginPassword.Password ?? "";
        }

        private void btnBrowseWowExe_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Wow.exe (3.3.5a client)",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };

            try
            {
                string current = ReloginSettings.Instance.WowExePath;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    string? directory = System.IO.Path.GetDirectoryName(current);
                    if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
                        dialog.InitialDirectory = directory;
                }
            }
            catch { /* ignore, the dialog just opens in the last folder */ }

            if (dialog.ShowDialog(this) == true)
            {
                ReloginSettings.Instance.WowExePath = dialog.FileName;

                // ReloginSettings has no change notification, so push the value into the
                // text box as well instead of waiting for a reload of the window.
                try { txtWowExePath.Text = dialog.FileName; } catch { /* ignore */ }
            }
        }

        private void btnClassConfig_Click(object sender, RoutedEventArgs e)
        {
            var routine = Styx.Logic.Combat.RoutineManager.Current;
            if (routine != null && routine.WantButton)
                routine.OnButtonPress();
        }

        private void btnBotConfig_Click(object sender, RoutedEventArgs e)
        {
            if (BotManager.Current == null) return;
            var configWindow = BotManager.Current.ConfigurationWindow;
            if (configWindow != null) { configWindow.Owner = this; configWindow.ShowDialog(); return; }
            var configForm = BotManager.Current.ConfigurationForm;
            if (configForm == null) return;
            configForm.ShowDialog();
        }

        private void btnPlugins_Click(object sender, RoutedEventArgs e)
        {
            new PluginsWindow { Owner = this }.ShowDialog();
        }

        private void btnReportBug_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://discord.gg/ep5TcGMCcB",
                UseShellExecute = true
            });
        }

        private void btnDevTools_Click(object sender, RoutedEventArgs e)
        {
            new DeveloperToolsWindow().Show();
        }

        }

    /// <summary>
    /// Data context for binding both CharacterSettings and StyxSettings
    /// </summary>
    public class SettingsDataContext
    {
        public CharacterSettings? CharSettings { get; set; }
        public StyxSettings? StyxSettings { get; set; }
        public ReloginSettings? Relogin { get; set; }
    }
}

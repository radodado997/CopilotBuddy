using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Xml;
using MahApps.Metro.Controls;
using Styx.Helpers;
using Styx.Localization;
using Styx;
using Styx.Logic.Relogin;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// Settings window for CopilotBuddy, similar to HB WoD settings
    /// </summary>
    public partial class SettingsWindow : MetroWindow
    {
        private readonly ReloginSettings? _reloginDraft;
        private bool _reloginPasswordModified;
        private string? _reloginLoadErrorMessage;

        public SettingsWindow()
        {
            InitializeComponent();

            try
            {
                _reloginDraft = ReloginSettings.Instance.CreateDraft();
            }
            catch (XmlException ex)
            {
                _reloginDraft = null;
                Logging.Write("[Relogin] ReloginSettings.xml is malformed ({0}). Auto-Relogin editing is disabled; move {1} aside and reopen Settings.",
                    ex.Message, ReloginSettings.Instance.SettingsPath);
                _reloginLoadErrorMessage = string.Format(
                    CultureInfo.CurrentUICulture,
                    Globalization.Get("ReloginMalformedFileMessage"),
                    ReloginSettings.Instance.SettingsPath);
            }
            catch (ReloginSettingsScrubFailedException ex)
            {
                _reloginDraft = null;
                Logging.Write("[Relogin] Could not rewrite {0} to remove the legacy password ({1}). Auto-Relogin editing is disabled; restore write access and reopen Settings.",
                    ex.SettingsPath, ex.Message);
                _reloginLoadErrorMessage = string.Format(
                    CultureInfo.CurrentUICulture,
                    Globalization.Get("ReloginScrubFailedMessage"),
                    ex.SettingsPath);
            }
            catch (Exception ex)
            {
                _reloginDraft = null;
                Logging.Write("[Relogin] Could not load relogin settings for editing ({0}). Auto-Relogin editing is disabled.",
                    ex.Message);
                _reloginLoadErrorMessage = string.Format(
                    CultureInfo.CurrentUICulture,
                    Globalization.Get("ReloginLoadFailedMessage"),
                    ex.Message, ReloginSettings.Instance.SettingsPath);
            }
            
            // Set DataContext to enable bindings to CharacterSettings and StyxSettings
            DataContext = new SettingsDataContext
            {
                CharSettings = CharacterSettings.Instance,
                StyxSettings = StyxSettings.Instance,
                ReloginSettings = _reloginDraft
            };

            // Auto-relogin password is not bindable (PasswordBox intentionally exposes no
            // Text DP), so it is pushed into the control and read back on save.
            if (_reloginDraft != null)
            {
                pwdReloginPassword.Password = _reloginDraft.Password ?? "";
                pwdReloginPassword.PasswordChanged += (_, _) => _reloginPasswordModified = true;
            }
            else
            {
                gbRelogin.IsEnabled = false;
                gbReloginAdvanced.IsEnabled = false;
                pwdReloginPassword.Password = "";
                if (_reloginLoadErrorMessage != null)
                {
                    string message = _reloginLoadErrorMessage;
                    string title = Globalization.Get("ReloginSaveFailedTitle");
                    Loaded += (_, _) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

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
            if (_reloginDraft != null)
            {
                string invalidTitle = Globalization.Get("ReloginInvalidSettingTitle");
                foreach (var field in new[] { txtReloginRetryDelay, txtReloginMaxRetries })
                {
                    field.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    if (Validation.GetHasError(field))
                    {
                        string name = Globalization.Get(field == txtReloginRetryDelay ? "ReloginRetryDelay" : "ReloginMaxRetries");
                        string msg = string.Format(CultureInfo.CurrentUICulture, Globalization.Get("ReloginMustBeWholeNumberFormat"), name);
                        MessageBox.Show(this, msg, invalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                        field.Focus();
                        return;
                    }
                }
                if (_reloginDraft.RetryDelaySeconds < 1 || _reloginDraft.MaxRetryCount < 0)
                {
                    MessageBox.Show(this, Globalization.Get("ReloginRetryRangeError"), invalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string saveFailedTitle = Globalization.Get("ReloginSaveFailedTitle");
                string enteredPassword = pwdReloginPassword.Password ?? "";
                bool passwordModified = _reloginDraft.IsCredentialUnavailable
                    ? _reloginPasswordModified
                    : !string.Equals(enteredPassword, _reloginDraft.Password ?? "", StringComparison.Ordinal);
                try { ReloginSettings.Instance.CommitFrom(_reloginDraft, enteredPassword, passwordModified: passwordModified); }
                catch (ReloginSettingsConflictException)
                {
                    MessageBox.Show(this, Globalization.Get("ReloginSaveConflictMessage"), saveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                catch (ReloginSettingsMalformedException ex)
                {
                    // Do not let a malformed ReloginSettings.xml prevent saving unrelated settings.
                    CharacterSettings.Instance.Save();
                    StyxSettings.Instance.Save();
                    string msg = string.Format(CultureInfo.CurrentUICulture, Globalization.Get("ReloginMalformedFileMessage"), ex.SettingsPath);
                    MessageBox.Show(this, msg, saveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                catch (Exception)
                {
                    MessageBox.Show(this, Globalization.Get("ReloginSaveFailedMessage"), saveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // Save both settings
            CharacterSettings.Instance.Save();
            StyxSettings.Instance.Save();

            // Apply immediately only when a valid relogin draft was committed.
            if (_reloginDraft != null)
                ReloginManager.Start();

            // Close the window
            Close();
        }

        private void btnReloginBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (_reloginDraft == null)
                return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Wow.exe",
                Filter = "Wow client (Wow.exe)|Wow.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                _reloginDraft.WowExecutablePath = dialog.FileName;
                // Refresh the TextBox: the OpenFileDialog does not go through the binding.
                txtReloginWowPath.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
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
        public ReloginSettings? ReloginSettings { get; set; }
    }
}

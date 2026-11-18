using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Styx.Helpers
{
    internal sealed class ReloginSettingsConflictException : InvalidOperationException
    {
        internal ReloginSettingsConflictException()
            : base("Relogin settings changed since this draft was opened.")
        {
        }
    }

    internal sealed class ReloginSettingsMalformedException : InvalidOperationException
    {
        internal string SettingsPath { get; }

        internal ReloginSettingsMalformedException(string settingsPath, Exception innerException)
            : base("ReloginSettings.xml is malformed. Move it aside and retry saving.", innerException)
        {
            SettingsPath = settingsPath;
        }
    }

    internal sealed class ReloginSettingsScrubFailedException : IOException
    {
        internal string SettingsPath { get; }
        internal bool HasValidVaultCredential { get; }

        internal ReloginSettingsScrubFailedException(string settingsPath, bool hasValidVaultCredential, Exception innerException)
            : base(innerException.Message, innerException)
        {
            SettingsPath = settingsPath;
            HasValidVaultCredential = hasValidVaultCredential;
        }
    }

    /// <summary>
    /// Auto-relogin settings (global, not per-character).
    /// Stored in Settings/ReloginSettings.xml, next to StyxSettings.xml / UISettings.xml.
    ///
    /// Passwords live in the current Windows user's Credential Manager vault; XML contains
    /// only a reference. Legacy plaintext entries are migrated on first load.
    ///
    /// Credentials are only needed to drive the 3.3.5a glue screens
    /// (DefaultServerLogin / ChangeRealm / CharacterSelect), i.e. the same values HBRelog
    /// keeps in its own profile XML.
    /// </summary>
    public class ReloginSettings : Settings
    {
        /// <summary>
        /// Singleton instance. Loaded lazily on first access (no WoW attachment required).
        /// </summary>
        public static readonly ReloginSettings Instance = new ReloginSettings();

        private string _settingsRevision = "";
        private bool _credentialUnavailable;
        private bool _pendingLegacyScrub;

        internal bool IsCredentialUnavailable => _credentialUnavailable;

        public ReloginSettings()
            : base(Path.Combine(Logging.ApplicationPath, "Settings", "ReloginSettings.xml"), null, autoInitialize: false)
        {
            string? snapshotRevision = null;
            try
            {
                ExecuteWithSettingsLock(() =>
                {
                    if (!File.Exists(SettingsPath))
                    {
                        _settingsRevision = WriteSettings(GetXML());
                        Password = "";
                        _credentialUnavailable = false;
                        _pendingLegacyScrub = false;
                        return;
                    }

                    byte[] fileBytes = File.ReadAllBytes(SettingsPath);
                    string fileRevision = ComputeBytesRevision(fileBytes);
                    snapshotRevision = fileRevision;
                    LoadOrMigrateSnapshotLocked(fileBytes, fileRevision, allowVaultFallback: false, out _);
                });
            }
            catch (ReloginSettingsScrubFailedException ex)
            {
                // Fail closed without publishing unsanitized XML; instruct the user to restore
                // write access. Only state that password re-entry is unnecessary when a valid
                // vault credential actually resolved.
                Enabled = false;
                _pendingLegacyScrub = true;
                if (!ex.HasValidVaultCredential)
                {
                    Password = "";
                    _credentialUnavailable = true;
                }
                if (snapshotRevision != null)
                    _settingsRevision = snapshotRevision;
                else
                    try { _settingsRevision = ComputeFileRevision(SettingsPath); } catch { _settingsRevision = ""; }

                if (ex.HasValidVaultCredential)
                {
                    Logging.Write("[Relogin] Could not scrub the legacy Password element from {0} ({1}); legacy plaintext remains on disk and auto-relogin is disabled. Restore write access to {0} and restart or reopen Settings (your saved vault password does not need to be re-entered).",
                        SettingsPath, ex.Message);
                }
                else
                {
                    Logging.Write("[Relogin] Could not scrub the legacy Password element from {0} ({1}); legacy XML remains on disk and auto-relogin is disabled. Restore write access to {0} and restart or reopen Settings.",
                        SettingsPath, ex.Message);
                }
            }
            catch (Exception)
            {
                Enabled = false;
                Password = "";
                _credentialUnavailable = true;
                if (snapshotRevision != null)
                    _settingsRevision = snapshotRevision;
                else
                    try { _settingsRevision = ComputeFileRevision(SettingsPath); } catch { _settingsRevision = ""; }
                Logging.Write("[Relogin] Credentials could not be loaded or migrated; auto-relogin is disabled. Re-enter the password and save settings.");
            }
        }

        /// <summary>
        /// Returns a consistent snapshot of all settings (including AccountName and Password)
        /// captured under the same cross-process settings mutex used by ReloadFromDiskLocked and CommitFrom.
        /// </summary>
        internal ReloginSettings Snapshot()
        {
            ReloginSettings? copy = null;
            ExecuteWithSettingsLock(() => copy = (ReloginSettings)MemberwiseClone());
            return copy!;
        }

        internal ReloginSettings CreateDraft()
        {
            ReloginSettings? draft = null;
            ExecuteWithSettingsLock(() =>
            {
                string diskRevision = ComputeFileRevision(SettingsPath);
                if (!string.Equals(diskRevision, _settingsRevision, StringComparison.Ordinal)
                    || _credentialUnavailable
                    || _pendingLegacyScrub)
                {
                    ReloadFromDiskLocked(out ReloginSettings? vaultFallbackDraft);
                    if (vaultFallbackDraft != null)
                    {
                        draft = vaultFallbackDraft;
                        return;
                    }
                }
                draft = (ReloginSettings)MemberwiseClone();
            });
            return draft!;
        }

        internal void CommitFrom(ReloginSettings draft, string password, bool allowMalformedRepair = false, bool? passwordModified = null)
        {
            password ??= "";
            ExecuteWithSettingsLock(() =>
            {
                byte[]? diskBytes = File.Exists(SettingsPath) ? File.ReadAllBytes(SettingsPath) : null;
                string diskRevision = diskBytes != null ? ComputeBytesRevision(diskBytes) : "";

                string diskReference = "";
                string? diskLegacyPassword = null;
                bool xmlMalformed = false;
                XmlException? malformedError = null;
                if (diskBytes != null)
                {
                    try
                    {
                        XElement diskRoot = LoadXmlFromBytes(diskBytes);
                        diskReference = diskRoot.Element(nameof(PasswordReference))?.Value ?? "";
                        XElement? diskLegacy = diskRoot.Element("Password");
                        if (diskLegacy != null && !string.IsNullOrEmpty(diskLegacy.Value))
                            diskLegacyPassword = diskLegacy.Value;
                    }
                    catch (XmlException ex)
                    {
                        xmlMalformed = true;
                        malformedError = ex;
                    }
                }

                if (xmlMalformed)
                {
                    if (!allowMalformedRepair || !string.Equals(diskRevision, draft._settingsRevision, StringComparison.Ordinal))
                        throw new ReloginSettingsMalformedException(SettingsPath, malformedError!);
                }
                else if (!string.Equals(diskRevision, draft._settingsRevision, StringComparison.Ordinal)
                    || !string.Equals(diskReference, draft.PasswordReference, StringComparison.Ordinal))
                {
                    try { ReloadFromDiskLocked(out _); } catch { }
                    throw new ReloginSettingsConflictException();
                }

                string reference;
                string publishedPassword = password;
                bool publishedUnavailable = false;
                bool wroteNewReference = false;

                if (draft._credentialUnavailable)
                {
                    // On an unavailable draft, the password box starts blank. When untouched,
                    // preserve the stored PasswordReference (and resolve it if the vault has
                    // recovered) or require a pending legacy XML Password to migrate cleanly.
                    // Explicit replacements or clears proceed without preserving legacy values.
                    if (string.IsNullOrEmpty(password))
                    {
                        if (passwordModified != true && !xmlMalformed)
                        {
                            if (!string.IsNullOrEmpty(diskReference))
                            {
                                string resolvedPassword = "";
                                bool readSucceeded = false;
                                Exception? readError = null;
                                try
                                {
                                    resolvedPassword = ReloginCredentialStore.Read(diskReference);
                                    readSucceeded = true;
                                }
                                catch (Exception ex)
                                {
                                    readError = ex;
                                }

                                if (readSucceeded && !string.IsNullOrEmpty(resolvedPassword))
                                {
                                    reference = diskReference;
                                    publishedPassword = resolvedPassword;
                                    publishedUnavailable = false;
                                }
                                else if (readSucceeded && !string.IsNullOrEmpty(diskLegacyPassword))
                                {
                                    reference = ReloginCredentialStore.Write(diskLegacyPassword);
                                    wroteNewReference = true;
                                    publishedPassword = diskLegacyPassword;
                                    publishedUnavailable = false;
                                }
                                else if (!readSucceeded && !string.IsNullOrEmpty(diskLegacyPassword))
                                {
                                    // Vault read failed while XML still contains both a vault reference
                                    // and a legacy password: stop before writing XML so the existing
                                    // file is preserved intact.
                                    ExceptionDispatchInfo.Capture(readError!).Throw();
                                    return;
                                }
                                else
                                {
                                    reference = diskReference;
                                    publishedPassword = "";
                                    publishedUnavailable = true;
                                }
                            }
                            else if (!string.IsNullOrEmpty(diskLegacyPassword))
                            {
                                reference = ReloginCredentialStore.Write(diskLegacyPassword);
                                wroteNewReference = true;
                                publishedPassword = diskLegacyPassword;
                                publishedUnavailable = false;
                            }
                            else
                            {
                                reference = "";
                            }
                        }
                        else
                        {
                            reference = "";
                        }
                    }
                    else
                    {
                        reference = ReloginCredentialStore.Write(password);
                        wroteNewReference = true;
                    }
                }
                else
                {
                    bool passwordUntouched = passwordModified != true
                        && !xmlMalformed
                        && !string.IsNullOrEmpty(diskReference)
                        && string.Equals(password, draft.Password, StringComparison.Ordinal);

                    if (passwordUntouched)
                    {
                        string storedPassword = ReloginCredentialStore.Read(diskReference);
                        if (string.Equals(storedPassword, password, StringComparison.Ordinal))
                        {
                            reference = string.IsNullOrEmpty(password) ? "" : diskReference;
                        }
                        else
                        {
                            // The vault credential changed in place or was removed since the draft
                            // was opened; report a conflict rather than overwriting or silently
                            // restoring it with the stale dialog value.
                            try { ReloadFromDiskLocked(out _); } catch { }
                            throw new ReloginSettingsConflictException();
                        }
                    }
                    else if (string.IsNullOrEmpty(password))
                    {
                        reference = "";
                    }
                    else
                    {
                        reference = ReloginCredentialStore.Write(password);
                        wroteNewReference = true;
                    }
                }

                // Write the complete draft before mutating the singleton or restarting its monitor.
                XElement xml = draft.GetXML();
                xml.SetElementValue(nameof(PasswordReference), reference);
                string committedRevision;
                try { committedRevision = WriteSettings(xml); }
                catch
                {
                    if (wroteNewReference)
                        TryDeleteCredentialWithWarning(reference, "provisional");
                    throw;
                }

                string previousReference = diskReference;
                CopySettingsFrom(draft);
                PasswordReference = reference;
                Password = publishedPassword;
                _credentialUnavailable = publishedUnavailable;
                _pendingLegacyScrub = false;
                _settingsRevision = committedRevision;
                if (!string.IsNullOrEmpty(previousReference) && previousReference != reference)
                    TryDeleteCredentialWithWarning(previousReference, "previous");
            });
        }

        private void CopySettingsFrom(ReloginSettings source)
        {
            foreach (var property in typeof(ReloginSettings).GetProperties())
                if (property.IsDefined(typeof(SettingAttribute), false))
                    property.SetValue(this, property.GetValue(source));
        }

        private static void TryDeleteCredentialWithWarning(string reference, string label)
        {
            if (string.IsNullOrEmpty(reference))
                return;
            try
            {
                if (!ReloginCredentialStore.TryDelete(reference, out string? error))
                    Logging.Write("[Relogin] Warning: could not remove {0} Windows credential entry: {1}", label, error ?? "unknown error");
            }
            catch (Exception ex)
            {
                Logging.Write("[Relogin] Warning: could not remove {0} Windows credential entry: {1}", label, ex.Message);
            }
        }

        private void ExecuteWithSettingsLock(Action action)
        {
            string fullPath = Path.GetFullPath(SettingsPath).ToUpperInvariant();
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
            using var mutex = new Mutex(false, "Local\\CopilotBuddy.ReloginSettings." + hash);
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }

                if (!acquired)
                    throw new TimeoutException("Timed out waiting for the relogin settings lock.");

                action();
            }
            finally
            {
                if (acquired)
                    try { mutex.ReleaseMutex(); } catch { }
            }
        }

        private void ReloadFromDiskLocked(out ReloginSettings? vaultFallbackDraft)
        {
            vaultFallbackDraft = null;
            if (!File.Exists(SettingsPath))
            {
                InitializeDefaultValues();
                Password = "";
                _credentialUnavailable = false;
                _settingsRevision = "";
                return;
            }

            byte[] fileBytes = File.ReadAllBytes(SettingsPath);
            string fileRevision = ComputeBytesRevision(fileBytes);
            LoadOrMigrateSnapshotLocked(fileBytes, fileRevision, allowVaultFallback: true, out vaultFallbackDraft);
        }

        private void LoadOrMigrateSnapshotLocked(byte[] fileBytes, string fileRevision, bool allowVaultFallback, out ReloginSettings? vaultFallbackDraft)
        {
            vaultFallbackDraft = null;
            XElement root = LoadXmlFromBytes(fileBytes);
            XElement? legacy = root.Element("Password");

            var staged = (ReloginSettings)MemberwiseClone();
            staged.InitializeDefaultValues();
            staged.LoadFromXML(root);

            if (legacy != null)
            {
                // If a vault reference is already present alongside legacy plaintext, preserve
                // that reference if it resolves cleanly and scrub the plaintext from disk.
                if (!string.IsNullOrEmpty(staged.PasswordReference))
                {
                    string existingPassword;
                    try
                    {
                        existingPassword = ReloginCredentialStore.Read(staged.PasswordReference);
                    }
                    catch (Exception ex) when (allowVaultFallback)
                    {
                        Logging.WriteDebug("[Relogin] Could not resolve saved credential while scrubbing legacy XML ({0}); keeping live configuration unchanged.", ex.Message);
                        staged.Password = "";
                        staged._settingsRevision = fileRevision;
                        staged._credentialUnavailable = true;
                        vaultFallbackDraft = staged;
                        return;
                    }

                    if (!string.IsNullOrEmpty(existingPassword) || string.IsNullOrEmpty(legacy.Value))
                    {
                        root.Elements("Password").Remove();
                        string scrubbedRevision;
                        try
                        {
                            scrubbedRevision = WriteSettings(root); // Do not publish unless scrubbed XML is saved.
                        }
                        catch (Exception ex)
                        {
                            throw new ReloginSettingsScrubFailedException(
                                SettingsPath,
                                hasValidVaultCredential: !string.IsNullOrEmpty(existingPassword),
                                ex);
                        }
                        CopySettingsFrom(staged);
                        Password = existingPassword;
                        _credentialUnavailable = false;
                        _pendingLegacyScrub = false;
                        _settingsRevision = scrubbedRevision;
                        return;
                    }
                }

                string legacyPassword = legacy.Value;
                string provisionalReference = "";
                if (!string.IsNullOrEmpty(legacyPassword))
                {
                    try
                    {
                        provisionalReference = ReloginCredentialStore.Write(legacyPassword);
                    }
                    catch (Exception ex) when (allowVaultFallback)
                    {
                        // Vault write failed: keep the original file intact (retaining legacy
                        // password until explicitly replaced/cleared) and return a fallback draft.
                        Logging.WriteDebug("[Relogin] Could not migrate legacy credential to vault ({0}); returning fallback draft.", ex.Message);
                        staged.Password = "";
                        staged._settingsRevision = fileRevision;
                        staged._credentialUnavailable = true;
                        vaultFallbackDraft = staged;
                        return;
                    }
                }

                root.Elements("Password").Remove();
                root.SetElementValue(nameof(PasswordReference), provisionalReference);
                string migratedRevision;
                try
                {
                    migratedRevision = WriteSettings(root); // Do not claim migration completed unless disk is scrubbed.
                }
                catch (Exception ex)
                {
                    TryDeleteCredentialWithWarning(provisionalReference, "provisional");
                    throw new ReloginSettingsScrubFailedException(SettingsPath, hasValidVaultCredential: false, ex);
                }

                CopySettingsFrom(staged);
                PasswordReference = provisionalReference;
                Password = legacyPassword;
                _credentialUnavailable = false;
                _pendingLegacyScrub = false;
                _settingsRevision = migratedRevision;
                return;
            }

            string resolvedPassword;
            try
            {
                resolvedPassword = ReloginCredentialStore.Read(staged.PasswordReference);
            }
            catch (Exception ex) when (allowVaultFallback)
            {
                Logging.WriteDebug("[Relogin] Could not resolve saved credential while refreshing settings ({0}); keeping live configuration unchanged.", ex.Message);
                staged.Password = "";
                staged._settingsRevision = fileRevision;
                staged._credentialUnavailable = true;
                vaultFallbackDraft = staged;
                return;
            }

            CopySettingsFrom(staged);
            Password = resolvedPassword;
            _credentialUnavailable = false;
            _pendingLegacyScrub = false;
            _settingsRevision = fileRevision;
        }

        private static XElement LoadXmlFromBytes(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return XElement.Load(stream);
        }

        private static string ComputeBytesRevision(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private static string ComputeFileRevision(string path)
        {
            if (!File.Exists(path))
                return "";
            return ComputeBytesRevision(File.ReadAllBytes(path));
        }

        private string WriteSettings(XElement xml)
        {
            string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] stagedBytes;
                using (var ms = new MemoryStream())
                {
                    xml.Save(ms);
                    stagedBytes = ms.ToArray();
                }
                string stagedRevision = ComputeBytesRevision(stagedBytes);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllBytes(temp, stagedBytes);
                File.Move(temp, SettingsPath, true);
                return stagedRevision;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        #region Master switch

        private bool _enabled;

        /// <summary>
        /// Master switch for the auto-relogin feature. When false, nothing in this class is
        /// read and no monitor thread is started — behaviour is identical to a build without
        /// the feature.
        /// </summary>
        [DefaultValue(false)]
        [Setting(Explanation = "Automatically log back in after a crash, exit, or logout/disconnect while the bot is running.")]
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        #endregion

        #region Credentials / target

        private string _accountName = "";
        private string _password = "";

        [DefaultValue("")]
        [Setting]
        public string PasswordReference { get; set; } = "";
        private string _realmName = "";
        private string _characterName = "";

        /// <summary>
        /// Account name passed to DefaultServerLogin (3.3.5a has no Battle.net e-mail).
        /// </summary>
        [DefaultValue("")]
        [Setting]
        public string AccountName
        {
            get { return _accountName; }
            set { _accountName = value ?? ""; }
        }

        /// <summary>
        /// Account password passed to DefaultServerLogin.
        /// </summary>
        [DefaultValue("")]
        public string Password
        {
            get { return _password; }
            set { _password = value ?? ""; }
        }

        /// <summary>
        /// Realm (server) name as shown in the realm list. Empty = do not switch realms.
        /// </summary>
        [DefaultValue("")]
        [Setting]
        public string RealmName
        {
            get { return _realmName; }
            set { _realmName = value ?? ""; }
        }

        /// <summary>
        /// Character to select on the character-selection screen before entering the world.
        /// </summary>
        [DefaultValue("")]
        [Setting]
        public string CharacterName
        {
            get { return _characterName; }
            set { _characterName = value ?? ""; }
        }

        #endregion

        #region Client executable

        private string _wowExecutablePath = "";

        /// <summary>
        /// Full path to Wow.exe. Only used when the client process is gone; an already
        /// running client is never relaunched.
        /// </summary>
        [DefaultValue("")]
        [Setting(Explanation = "Full path to Wow.exe used to relaunch the client after a crash.")]
        public string WowExecutablePath
        {
            get { return _wowExecutablePath; }
            set { _wowExecutablePath = value ?? ""; }
        }

        #endregion

        #region Retry / timing

        private int _retryDelaySeconds = 30;
        private int _maxRetryCount = 3;
        private int _loginScreenTimeoutSeconds = 120;
        private int _inWorldTimeoutSeconds = 180;
        private int _hangGraceSeconds = 45;
        private int _loginRejectSeconds = 20;

        /// <summary>
        /// Seconds to wait before the next relogin attempt after a failed one.
        /// </summary>
        [DefaultValue(30)]
        [Setting(Explanation = "Seconds between relogin attempts.")]
        public int RetryDelaySeconds
        {
            get { return _retryDelaySeconds; }
            set { _retryDelaySeconds = value; }
        }

        /// <summary>
        /// Maximum number of relogin attempts per crash. Once exhausted the loop stops and
        /// the reason is reported in the log; it is re-armed on the next Start / settings save.
        /// </summary>
        [DefaultValue(3)]
        [Setting(Explanation = "Maximum relogin attempts per crash before giving up.")]
        public int MaxRetryCount
        {
            get { return _maxRetryCount; }
            set { _maxRetryCount = value; }
        }

        /// <summary>
        /// Seconds to wait for the client to reach the login/character-selection screen after
        /// the process was started (and the EndScene hook installed).
        /// </summary>
        [DefaultValue(120)]
        [Setting]
        public int LoginScreenTimeoutSeconds
        {
            get { return _loginScreenTimeoutSeconds; }
            set { _loginScreenTimeoutSeconds = value; }
        }

        /// <summary>
        /// Seconds to wait for the in-world state after EnterWorld was issued.
        /// </summary>
        [DefaultValue(180)]
        [Setting]
        public int InWorldTimeoutSeconds
        {
            get { return _inWorldTimeoutSeconds; }
            set { _inWorldTimeoutSeconds = value; }
        }

        /// <summary>
        /// How long the client has to stay completely unresponsive before it is treated as
        /// crashed rather than lagging. 0 disables hang detection (process exit only).
        /// </summary>
        [DefaultValue(45)]
        [Setting(Explanation = "Seconds of continuous non-responsiveness before the client is considered crashed. 0 = process exit only.")]
        public int HangGraceSeconds
        {
            get { return _hangGraceSeconds; }
            set { _hangGraceSeconds = value; }
        }

        /// <summary>
        /// A login rejected by the server (wrong account/password) bounces back to the login
        /// form within a few seconds; a server that is unreachable takes much longer to fail.
        /// The threshold separates the two cases.
        /// </summary>
        [DefaultValue(20)]
        [Setting(Explanation = "Login attempts that fail faster than this are treated as rejected credentials instead of an unreachable server.")]
        public int LoginRejectSeconds
        {
            get { return _loginRejectSeconds; }
            set { _loginRejectSeconds = value; }
        }

        #endregion
    }
}

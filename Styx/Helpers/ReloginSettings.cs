using System.ComponentModel;
using System.IO;

namespace Styx.Helpers
{
    /// <summary>
    /// Auto-relogin settings (global, not per-character).
    /// Stored in Settings/ReloginSettings.xml, next to StyxSettings.xml / UISettings.xml.
    ///
    /// Values are persisted in plain text, exactly like every other value the bot stores
    /// (MailRecipient, LastUsedPath, ...).  The project does ship an
    /// <see cref="EncryptedAttribute"/>, but no settings class uses it anywhere in this
    /// repository, so there is no existing encrypted store or OS credential store to hook
    /// into; adding one here would also make the file unreadable for the hand-editing
    /// workflow the rest of Settings/ relies on.
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

        public ReloginSettings()
            : base(Path.Combine(SettingsDirectory, "ReloginSettings.xml"))
        {
        }

        #region Master switch

        private bool _enabled;

        /// <summary>
        /// Master switch for the auto-relogin feature. When false, nothing in this class is
        /// read and no monitor thread is started — behaviour is identical to a build without
        /// the feature.
        /// </summary>
        [DefaultValue(false)]
        [Setting(Explanation = "Automatically relaunch and log the WoW client back in after it crashes or exits.")]
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        #endregion

        #region Credentials / target

        private string _accountName = "";
        private string _password = "";
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
        [Setting]
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

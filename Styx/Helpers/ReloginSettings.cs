using System;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Styx.Helpers
{
    /// <summary>
    /// Global (not per-character) settings for the Auto Relogin feature.
    /// Stored in Settings/ReloginSettings.xml next to CopilotBuddy.exe.
    ///
    /// Auto Relogin has two jobs:
    ///   1. game client still running but the character was dropped (disconnect)
    ///      -&gt; return to the login screen, type the credentials, enter the world;
    ///   2. game client gone (crash / closed) -&gt; start Wow.exe, log in, enter the
    ///      world, and let the bot re-attach afterwards.
    ///
    /// The password is stored AES encrypted (Base64) in the settings file.  The key
    /// is baked into the binary, so this is obfuscation, not real protection: anyone
    /// who can read the file and knows the format can recover the password.  Use a
    /// dedicated account - or leave the feature disabled - if that matters to you.
    /// </summary>
    public class ReloginSettings : Settings
    {
        public static readonly ReloginSettings Instance = new ReloginSettings();

        public ReloginSettings()
            : base(Path.Combine(SettingsDirectory, "ReloginSettings.xml"))
        {
        }

        #region Master switch / credentials

        /// <summary>Master switch. When false the monitor does nothing at all.</summary>
        [Setting]
        [DefaultValue(false)]
        public bool Enabled { get; set; } = false;

        /// <summary>Full path to the 3.3.5a client, e.g. C:\WoW\Wow.exe.</summary>
        [Setting]
        [DefaultValue("")]
        public string WowExePath { get; set; } = "";

        /// <summary>
        /// Optional command line for the client. Leave empty for none.
        /// The working directory is always the folder that contains WowExePath.
        /// </summary>
        [Setting]
        [DefaultValue("")]
        public string WowArguments { get; set; } = "";

        /// <summary>Account name typed into the login screen.</summary>
        [Setting]
        [DefaultValue("")]
        public string AccountName { get; set; } = "";

        /// <summary>
        /// Character to log in with. Informational only on 3.3.5a: the client has no
        /// name filter on the character select screen, so the bot always enters the
        /// world with the character the client already has selected (the last played
        /// one).  Kept so the intent is visible and for future use.
        /// </summary>
        [Setting]
        [DefaultValue("")]
        public string CharacterName { get; set; } = "";

        /// <summary>AES encrypted password, Base64. Use the Password property instead.</summary>
        [Setting]
        [DefaultValue("")]
        public string EncryptedPassword { get; set; } = "";

        /// <summary>Plain text password; encrypted transparently into EncryptedPassword.</summary>
        [Browsable(false)]
        public string Password
        {
            get
            {
                if (string.IsNullOrEmpty(EncryptedPassword))
                    return "";
                try
                {
                    return Decrypt(EncryptedPassword);
                }
                catch
                {
                    return "";
                }
            }
            set
            {
                EncryptedPassword = string.IsNullOrEmpty(value) ? "" : Encrypt(value);
            }
        }

        /// <summary>True when an account name and a password are both configured.</summary>
        [Browsable(false)]
        public bool HasCredentials =>
            !string.IsNullOrWhiteSpace(AccountName) && !string.IsNullOrEmpty(EncryptedPassword);

        /// <summary>True when the configured client executable exists on disk.</summary>
        [Browsable(false)]
        public bool HasClientPath =>
            !string.IsNullOrWhiteSpace(WowExePath) && File.Exists(WowExePath);

        #endregion

        #region Timings

        /// <summary>
        /// How long the character may stay out of the world before it counts as a
        /// disconnect. Loading screens and zoning stay below this, so keep it well
        /// above 30 seconds unless you know your zoning is fast.
        /// </summary>
        [Setting]
        [DefaultValue(60)]
        public int DisconnectGraceSeconds { get; set; } = 60;

        /// <summary>Pause before relaunching a crashed client.</summary>
        [Setting]
        [DefaultValue(15)]
        public int RelaunchDelaySeconds { get; set; } = 15;

        /// <summary>How long to wait for the client window after starting Wow.exe.</summary>
        [Setting]
        [DefaultValue(90)]
        public int ClientStartupTimeoutSeconds { get; set; } = 90;

        /// <summary>
        /// Delay between "credentials sent" and "character select is on screen".
        /// Raise it on slow machines / slow realms.
        /// </summary>
        [Setting]
        [DefaultValue(20)]
        public int CharSelectDelaySeconds { get; set; } = 20;

        /// <summary>How long to wait for the world to load after entering it.</summary>
        [Setting]
        [DefaultValue(120)]
        public int EnterWorldTimeoutSeconds { get; set; } = 120;

        /// <summary>Login attempts per disconnect before the monitor backs off.</summary>
        [Setting]
        [DefaultValue(4)]
        public int MaxAttemptsPerDisconnect { get; set; } = 4;

        /// <summary>Kill and restart the client when the first attempts fail.</summary>
        [Setting]
        [DefaultValue(true)]
        public bool RestartClientOnRepeatedFailure { get; set; } = true;

        /// <summary>Re-start the bot automatically once the character is back in game.</summary>
        [Setting]
        [DefaultValue(true)]
        public bool RestartBotAfterRelogin { get; set; } = true;

        /// <summary>How often the monitor checks the client state.</summary>
        [Setting]
        [DefaultValue(5)]
        public int MonitorIntervalSeconds { get; set; } = 5;

        #endregion

        #region Password crypto

        // Fixed key/IV: obfuscation only, see the class summary. 16 byte IV for AES-CBC.
        private static readonly byte[] CryptoKey =
            SHA256.HashData(Encoding.UTF8.GetBytes("CopilotBuddy.AutoRelogin.v1"));

        private static readonly byte[] CryptoIv =
            MD5.HashData(Encoding.UTF8.GetBytes("CopilotBuddyAutoReloginIV"));

        private static string Encrypt(string plainText)
        {
            using Aes aes = Aes.Create();
            aes.Key = CryptoKey;
            aes.IV = CryptoIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using ICryptoTransform encryptor = aes.CreateEncryptor();
            byte[] clear = Encoding.UTF8.GetBytes(plainText);
            byte[] cipher = encryptor.TransformFinalBlock(clear, 0, clear.Length);
            return Convert.ToBase64String(cipher);
        }

        private static string Decrypt(string base64Cipher)
        {
            using Aes aes = Aes.Create();
            aes.Key = CryptoKey;
            aes.IV = CryptoIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using ICryptoTransform decryptor = aes.CreateDecryptor();
            byte[] cipher = Convert.FromBase64String(base64Cipher);
            byte[] clear = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            return Encoding.UTF8.GetString(clear);
        }

        #endregion
    }
}

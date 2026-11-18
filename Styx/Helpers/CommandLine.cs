using System;

namespace Styx.Helpers
{
    /// <summary>
    /// Provides access to command-line arguments passed to the process.
    /// Ported from HB 4.3.4 Styx.Helpers.CommandLine.
    /// HbUser/HbPass removed — no auth in CopilotBuddy.
    /// </summary>
    public static class CommandLine
    {
        static CommandLine()
        {
            Arguments = new Arguments(Arguments.Tokenize(Environment.CommandLine));
        }

        /// <summary>Gets the parsed command-line arguments.</summary>
        public static Arguments Arguments { get; private set; }

        /// <summary>Path to a profile to load at startup (-loadprofile).</summary>
        public static string AutoLoadProfile => Arguments.GetString("loadprofile");

        /// <summary>Name of the bot to start automatically (-botname).</summary>
        public static string AutoStartBotName => Arguments.GetString("botname");

        /// <summary>Process ID to attach to automatically (-pid).</summary>
        public static int AutoAttachProcessId => Arguments.GetInt("pid");

        /// <summary>Name of the custom class to select automatically (-customclass).</summary>
        public static string AutoSelectCustomClass => Arguments.GetString("customclass");

        /// <summary>Optional named instance for isolated global settings (-instance).</summary>
        public static string? InstanceName => GetSingleValue("instance");

        /// <summary>Squad role override passed on CLI (-squadrole).</summary>
        public static string? SquadRole => GetSingleValue("squadrole");

        /// <summary>Whether this client is the squad coordinator (-squadcoordinator).</summary>
        public static bool? SquadCoordinator => GetNullableBool("squadcoordinator");

        /// <summary>Team identifier for IPC isolation (-squadteam).</summary>
        public static string? SquadTeam => GetSingleValue("squadteam");

        /// <summary>TCP loopback port for squad IPC (-squadport).</summary>
        public static int? SquadPort => GetNullableInt("squadport");

        /// <summary>
        /// Comma-separated roster of expected character names (-squadroster).
        /// Reads the full token collection because Arguments splits comma-separated values.
        /// </summary>
        public static string? SquadRoster => GetJoinedValues("squadroster");

        /// <summary>Optional explicit coordinator character name (-squadleader).</summary>
        public static string? SquadLeader => GetSingleValue("squadleader");

        /// <summary>
        /// Parses an explicit process ID from raw command line args supporting both /pid= and -pid=.
        /// Returns null when omitted or invalid.
        /// </summary>
        public static int? ParseProcessId(string[]? args = null)
        {
            if (args == null)
            {
                try
                {
                    if (AutoAttachProcessId > 0)
                        return AutoAttachProcessId;
                }
                catch
                {
                    // Fall through to safe raw argument inspection if -pid is malformed.
                }
            }

            args ??= Environment.GetCommandLineArgs();
            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg))
                    continue;

                if (arg.StartsWith("/pid=", StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith("-pid=", StringComparison.OrdinalIgnoreCase))
                {
                    string raw = arg.Substring(5).Trim();
                    if (int.TryParse(raw, out int parsed) && parsed > 0)
                        return parsed;
                }
            }

            return null;
        }

        private static string? GetSingleValue(string key)
        {
            var values = Arguments[key];
            if (values == null || values.Count == 0)
                return null;
            string joined = string.Join(",", values).Trim();
            return string.IsNullOrEmpty(joined) ? null : joined;
        }

        private static string? GetJoinedValues(string key)
        {
            var values = Arguments[key];
            if (values == null || values.Count == 0)
                return null;
            string joined = string.Join(",", values).Trim();
            return string.IsNullOrEmpty(joined) ? null : joined;
        }

        private static bool? GetNullableBool(string key)
        {
            if (!Arguments.Exists(key))
                return null;
            string? raw = GetSingleValue(key);
            if (raw == null)
                return Arguments.IsTrue(key) ? true : null;
            if (bool.TryParse(raw, out bool b))
                return b;
            if (raw == "1") return true;
            if (raw == "0") return false;
            return null;
        }

        private static int? GetNullableInt(string key)
        {
            string? raw = GetSingleValue(key);
            if (raw != null && int.TryParse(raw, out int value))
                return value;
            return null;
        }
    }
}

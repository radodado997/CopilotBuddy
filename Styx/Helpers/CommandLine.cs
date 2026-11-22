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
        public static string? InstanceName => !Arguments.Exists("instance") ? null
            : GetSingleValue("instance") ?? throw new ArgumentException("-instance requires a nonempty name.");

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

        public static string? SquadLeader => GetSingleValue("squadleader");

        public static string? SquadBotMode => GetSingleValue("squadmode");

        public static string? SquadDungeonEntry => GetSingleValue("dungeonentry");

        public static string? SquadDungeonIds => GetJoinedValues("dungeonids");

        /// <summary>
        /// Parses an explicit process ID from raw command line args supporting both /pid= and -pid=.
        /// Returns null when omitted and zero when explicitly invalid.
        /// </summary>
        public static int? ParseProcessId(string[]? args = null)
        {
            try
            {
                var parsedArguments = args == null ? Arguments : new Arguments(args);
                if (!parsedArguments.Exists("pid")) return null;
                if (!parsedArguments.HasOperand("pid")) return 0;
                string raw = parsedArguments.GetString("pid");
                return int.TryParse(raw, out int pid) && pid > 0 ? pid : 0;
            }
            catch (ArgumentException) { return 0; }
        }

        private static string? GetSingleValue(string key)
        {
            if (!Arguments.HasOperand(key)) return null;
            var values = Arguments[key];
            if (values == null || values.Count == 0)
                return null;
            string joined = string.Join(",", values).Trim();
            return string.IsNullOrEmpty(joined) ? null : joined;
        }

        private static string? GetJoinedValues(string key)
        {
            if (!Arguments.HasOperand(key)) return null;
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
                return Arguments.IsTrue(key)
                    ? true
                    : throw new ArgumentException($"{key} requires true, false, 1 or 0.");
            if (bool.TryParse(raw, out bool b))
                return b;
            if (raw == "1") return true;
            if (raw == "0") return false;
            throw new ArgumentException($"{key} has invalid value '{raw}'; expected true, false, 1 or 0.");
        }

        private static int? GetNullableInt(string key)
        {
            if (!Arguments.Exists(key))
                return null;
            string? raw = GetSingleValue(key);
            if (raw != null && int.TryParse(raw, out int value) && value > 0 && value <= 65535)
                return value;
            throw new ArgumentException(
                raw == null
                    ? $"{key} requires a port number."
                    : $"{key} has invalid value '{raw}'; expected a port number from 1 to 65535.");
        }
    }
}

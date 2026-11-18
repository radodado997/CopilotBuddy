using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Styx.Helpers
{
    /// <summary>Generic credentials in the current Windows user's Credential Manager vault.</summary>
    internal static class ReloginCredentialStore
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags, Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias, UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        private const int ErrorNotFound = 1168;

        internal static bool TryDelete(string reference, out string? error)
        {
            error = null;
            if (string.IsNullOrEmpty(reference) || !reference.StartsWith("CopilotBuddy/Relogin/", StringComparison.Ordinal))
                return true;

            if (CredDelete(reference, 1, 0))
                return true;

            int win32Error = Marshal.GetLastWin32Error();
            if (win32Error == ErrorNotFound)
                return true;

            error = new Win32Exception(win32Error).Message;
            return false;
        }

        internal static void Delete(string reference)
        {
            TryDelete(reference, out _);
        }

        internal static string Read(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return "";
            if (!reference.StartsWith("CopilotBuddy/Relogin/", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid relogin credential reference.");
            if (!CredRead(reference, 1, 0, out IntPtr pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1168) return ""; // Not present for this Windows user.
                throw new Win32Exception(error);
            }
            try
            {
                Credential credential = Marshal.PtrToStructure<Credential>(pointer);
                return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2)) ?? "";
            }
            finally { CredFree(pointer); }
        }

        internal static string Write(string password)
        {
            // A new reference makes a failed settings save leave the old credential intact.
            string reference = "CopilotBuddy/Relogin/" + Guid.NewGuid().ToString("N");
            IntPtr secret = Marshal.StringToCoTaskMemUni(password);
            try
            {
                var credential = new Credential
                {
                    Type = 1, TargetName = reference, UserName = Environment.UserName,
                    CredentialBlobSize = checked((uint)password.Length * 2),
                    CredentialBlob = secret, Persist = 2 // local machine, current user vault
                };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return reference;
            }
            finally { Marshal.ZeroFreeCoTaskMemUnicode(secret); }
        }
    }
}

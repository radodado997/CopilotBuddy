using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Styx.Logic.Relogin
{
    /// <summary>
    /// Thin Win32 layer used by the Auto Relogin feature: find / start / kill the
    /// game client, bring its window to the foreground and type into it, and read
    /// the "in game" flag of a client the bot is not (yet) attached to.
    ///
    /// Everything here is deliberately independent of GreenMagic.Memory: the whole
    /// point of Auto Relogin is to work while the bot has no valid attachment (glue
    /// screen, crashed client), so all it needs is a byte read and SendInput.
    /// </summary>
    internal static class ReloginNative
    {
        // The 3.3.5a client is called Wow.exe; some private-server builds ship the
        // same binary under a different name, so all of them are recognised.
        private static readonly string[] ClientProcessNames = { "Wow", "WoW", "Luacct" };

        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessTerminate = 0x0001;

        private const int SwRestore = 9;

        private const uint InputKeyboard = 1;
        private const uint KeyeventfKeyup = 0x0002;
        private const uint KeyeventfUnicode = 0x0004;

        public const ushort VkTab = 0x09;
        public const ushort VkReturn = 0x0D;
        public const ushort VkEscape = 0x1B;
        public const ushort VkDelete = 0x2E;
        public const ushort VkControl = 0x11;
        public const ushort VkShift = 0x10;
        public const ushort VkA = 0x41;

        [StructLayout(LayoutKind.Sequential)]
        private struct Keybdinput
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Mouseinput
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // The union is declared with every member so sizeof(INPUT) is right on x86
        // *and* x64 (SendInput rejects a wrong cbSize).
        [StructLayout(LayoutKind.Explicit)]
        private struct Input
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(4)] public Mouseinput mi;
            [FieldOffset(4)] public Keybdinput ki;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, uint baseAddress,
            [Out] byte[] buffer, IntPtr size, out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        #region Process handling

        /// <summary>
        /// Finds a 3.3.5a client. The configured executable wins; otherwise any
        /// process with a known client name is accepted.
        /// </summary>
        public static Process? FindClient(string? preferredExePath, int pinnedPid = 0)
        {
            // An explicitly pinned client (the /pid= target the bot was started with)
            // wins over everything else while it is alive, so the monitor never switches
            // to a second client that happens to be running.
            if (pinnedPid != 0)
            {
                try
                {
                    Process pinned = Process.GetProcessById(pinnedPid);
                    if (!pinned.HasExited)
                        return pinned;
                }
                catch
                {
                    // Gone: fall through to the normal scan.
                }
            }

            Process[] all;
            try
            {
                all = ClientProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
            }
            catch
            {
                return null;
            }

            if (all.Length == 0)
                return null;

            // Drop processes we cannot inspect (access denied) or that already exited.
            var alive = new List<Process>();
            foreach (Process p in all)
            {
                try
                {
                    if (!p.HasExited)
                        alive.Add(p);
                }
                catch
                {
                    // ignore
                }
            }

            if (alive.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(preferredExePath))
            {
                foreach (Process p in alive)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (path != null && string.Equals(path, preferredExePath, StringComparison.OrdinalIgnoreCase))
                            return p;
                    }
                    catch
                    {
                        // ignore, try the next one
                    }
                }
            }

            // OrderBy(StartTime) reads a property that throws for processes we cannot
            // query, so use a value that never throws instead.
            return alive.OrderBy(SafeProcessId).FirstOrDefault() ?? alive[0];
        }

        /// <summary>True when the process exists and has not exited.</summary>
        public static bool IsProcessAlive(int processId)
        {
            if (processId == 0)
                return false;

            try
            {
                using Process p = Process.GetProcessById(processId);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static int SafeProcessId(Process p)
        {
            try
            {
                return p.Id;
            }
            catch
            {
                return int.MaxValue;
            }
        }

        /// <summary>Starts the configured client. Returns null when it could not be started.</summary>
        public static Process? StartClient(string exePath, string arguments)
        {
            try
            {
                string? workingDirectory = System.IO.Path.GetDirectoryName(exePath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments ?? "",
                    WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                        ? Environment.CurrentDirectory
                        : workingDirectory,
                    UseShellExecute = false
                };

                Process? started = Process.Start(startInfo);
                if (started != null)
                {
                    try { started.EnableRaisingEvents = true; } catch { /* ignore */ }
                    Styx.Helpers.Logging.Write("[AutoRelogin] Started {0} (pid {1}).", exePath, started.Id);
                }
                return started;
            }
            catch (Exception ex)
            {
                Styx.Helpers.Logging.Write(System.Windows.Media.Colors.Red,
                    "[AutoRelogin] Could not start '{0}': {1}", exePath, ex.Message);
                return null;
            }
        }

        /// <summary>Best-effort terminate. Used when a client is stuck in a modal loop.</summary>
        public static bool KillClient(Process p)
        {
            try
            {
                if (p.HasExited)
                    return true;

                IntPtr handle = OpenProcess(ProcessTerminate, false, p.Id);
                if (handle == IntPtr.Zero)
                    return false;

                try
                {
                    return TerminateProcess(handle, 1);
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch (Exception ex)
            {
                Styx.Helpers.Logging.WriteDebug("[AutoRelogin] Kill failed: {0}", ex.Message);
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

        #endregion

        #region State probe

        /// <summary>
        /// Reads the client's "in game" flag without holding an attachment.
        /// Returns false when the process cannot be read (exited, protected, ...).
        /// </summary>
        public static bool TryReadInGame(Process process, out bool inGame)
        {
            inGame = false;

            IntPtr handle = IntPtr.Zero;
            try
            {
                if (process.HasExited)
                    return false;

                handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
                if (handle == IntPtr.Zero)
                    return false;

                byte[] buffer = new byte[1];
                if (!ReadProcessMemory(handle, Styx.Offsets.GlobalOffsets.InGame, buffer, (IntPtr)1, out _))
                    return false;

                inGame = buffer[0] != 0;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }
        }

        #endregion

        #region Window focus + keyboard

        /// <summary>
        /// Brings the client window forward and gives it the keyboard focus. WoW
        /// ignores SendInput that is not aimed at the focused window, so this runs
        /// before every keystroke batch.
        /// </summary>
        public static bool FocusWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return false;

            try
            {
                if (IsIconic(windowHandle))
                    ShowWindow(windowHandle, SwRestore);

                if (GetForegroundWindow() == windowHandle)
                    return true;

                IntPtr foreground = GetForegroundWindow();
                uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
                uint ourThread = GetCurrentThreadId();

                bool attached = false;
                if (foregroundThread != 0 && foregroundThread != ourThread)
                    attached = AttachThreadInput(ourThread, foregroundThread, true);

                try
                {
                    SetForegroundWindow(windowHandle);
                    BringWindowToTop(windowHandle);
                    SetFocus(windowHandle);
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(ourThread, foregroundThread, false);
                }

                Thread.Sleep(350);
                return GetForegroundWindow() == windowHandle;
            }
            catch (Exception ex)
            {
                Styx.Helpers.Logging.WriteDebug("[AutoRelogin] FocusWindow failed: {0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Types a string with KEYEVENTF_UNICODE events. Stops and returns false as soon as
        /// <paramref name="targetWindow"/> is no longer the foreground window, so a stray
        /// focus change can never type the credentials into another application.
        /// </summary>
        public static bool SendText(string text, IntPtr targetWindow)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            foreach (char c in text)
            {
                if (targetWindow != IntPtr.Zero && !IsForeground(targetWindow))
                    return false;

                SendUnicodeChar(c);
                Thread.Sleep(35);
            }

            return true;
        }

        /// <summary>True when the given window is the foreground window.</summary>
        public static bool IsForeground(IntPtr windowHandle)
        {
            return windowHandle != IntPtr.Zero && GetForegroundWindow() == windowHandle;
        }

        private static void SendUnicodeChar(char c)
        {
            var inputs = new Input[2];

            inputs[0].type = InputKeyboard;
            inputs[0].ki.wVk = 0;
            inputs[0].ki.wScan = c;
            inputs[0].ki.dwFlags = KeyeventfUnicode;

            inputs[1].type = InputKeyboard;
            inputs[1].ki.wVk = 0;
            inputs[1].ki.wScan = c;
            inputs[1].ki.dwFlags = KeyeventfUnicode | KeyeventfKeyup;

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        }

        /// <summary>Sends one virtual key press (down + up).</summary>
        public static void SendKey(ushort virtualKey)
        {
            var inputs = new Input[2];

            inputs[0].type = InputKeyboard;
            inputs[0].ki.wVk = virtualKey;
            inputs[0].ki.wScan = (ushort)MapVirtualKey(virtualKey, 0);
            inputs[0].ki.dwFlags = 0;

            inputs[1].type = InputKeyboard;
            inputs[1].ki.wVk = virtualKey;
            inputs[1].ki.wScan = (ushort)MapVirtualKey(virtualKey, 0);
            inputs[1].ki.dwFlags = KeyeventfKeyup;

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        }

        /// <summary>Ctrl+A, i.e. "select all" inside the account name field.</summary>
        public static void SendSelectAll()
        {
            // Sent as one batch: Ctrl held down across the A press, otherwise the
            // client sees two independent keystrokes instead of the chord.
            var inputs = new Input[4];

            inputs[0].type = InputKeyboard;
            inputs[0].ki.wVk = VkControl;
            inputs[0].ki.wScan = (ushort)MapVirtualKey(VkControl, 0);
            inputs[0].ki.dwFlags = 0;

            inputs[1].type = InputKeyboard;
            inputs[1].ki.wVk = VkA;
            inputs[1].ki.wScan = (ushort)MapVirtualKey(VkA, 0);
            inputs[1].ki.dwFlags = 0;

            inputs[2].type = InputKeyboard;
            inputs[2].ki.wVk = VkA;
            inputs[2].ki.wScan = (ushort)MapVirtualKey(VkA, 0);
            inputs[2].ki.dwFlags = KeyeventfKeyup;

            inputs[3].type = InputKeyboard;
            inputs[3].ki.wVk = VkControl;
            inputs[3].ki.wScan = (ushort)MapVirtualKey(VkControl, 0);
            inputs[3].ki.dwFlags = KeyeventfKeyup;

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        }

        /// <summary>Shift+Tab, i.e. move the caret to the previous control.</summary>
        public static void SendShiftTab()
        {
            // One batch: Shift held down across the Tab press, so the client sees the
            // chord rather than two independent keystrokes.
            var inputs = new Input[4];

            inputs[0].type = InputKeyboard;
            inputs[0].ki.wVk = VkShift;
            inputs[0].ki.wScan = (ushort)MapVirtualKey(VkShift, 0);
            inputs[0].ki.dwFlags = 0;

            inputs[1].type = InputKeyboard;
            inputs[1].ki.wVk = VkTab;
            inputs[1].ki.wScan = (ushort)MapVirtualKey(VkTab, 0);
            inputs[1].ki.dwFlags = 0;

            inputs[2].type = InputKeyboard;
            inputs[2].ki.wVk = VkTab;
            inputs[2].ki.wScan = (ushort)MapVirtualKey(VkTab, 0);
            inputs[2].ki.dwFlags = KeyeventfKeyup;

            inputs[3].type = InputKeyboard;
            inputs[3].ki.wVk = VkShift;
            inputs[3].ki.wScan = (ushort)MapVirtualKey(VkShift, 0);
            inputs[3].ki.dwFlags = KeyeventfKeyup;

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        }

        #endregion
    }
}

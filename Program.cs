namespace ClipboardMonitor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;

    internal class Program
    {
        private const int WmClipboardUpdate = 0x031D;
        private const int WmDestroy = 0x0002;
        private const uint CfUnicodeText = 13;
        private static readonly NativeMethods.WindowProc WindowProcedure = WindowProc;
        private static IntPtr monitorWindowHandle;
        private static long changeCounter;

        [STAThread]
        private static void Main(string[] args)
        {
            Console.Title = "Clipboard Monitor";
            PrintBanner();
            Console.WriteLine("Clipboard monitor started.");
            Console.WriteLine("Press Ctrl+C in this window to stop.");
            Console.WriteLine("Press Space to clear the screen.");
            Console.WriteLine();

            var windowClass = new NativeMethods.WndClass();
            windowClass.lpfnWndProc = WindowProcedure;
            windowClass.lpszClassName = "ClipboardMonitorWindowClass";

            var classAtom = NativeMethods.RegisterClass(ref windowClass);
            if (classAtom == 0)
            {
                Console.WriteLine("Failed to register monitor window. Error: {0}", Marshal.GetLastWin32Error());
                return;
            }

            monitorWindowHandle = NativeMethods.CreateWindowEx(
                0,
                windowClass.lpszClassName,
                "ClipboardMonitorWindow",
                0,
                0,
                0,
                0,
                0,
                NativeMethods.HwndMessage,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (monitorWindowHandle == IntPtr.Zero)
            {
                Console.WriteLine("Failed to create monitor window. Error: {0}", Marshal.GetLastWin32Error());
                return;
            }

            if (!NativeMethods.AddClipboardFormatListener(monitorWindowHandle))
            {
                Console.WriteLine("Failed to start clipboard listener. Error: {0}", Marshal.GetLastWin32Error());
                return;
            }

            Console.CancelKeyPress += OnCancelKeyPress;

            var hotkeyThread = new Thread(ListenForConsoleHotkeys);
            hotkeyThread.IsBackground = true;
            hotkeyThread.Start();

            NativeMethods.Msg message;
            while (NativeMethods.GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }

            NativeMethods.RemoveClipboardFormatListener(monitorWindowHandle);
            NativeMethods.DestroyWindow(monitorWindowHandle);
            monitorWindowHandle = IntPtr.Zero;
        }

        private static void PrintBanner()
        {
            var clippyText = new[]
            {
                "  _____ _ _                 ",
                " / ____| (_)                ",
                "| |    | |_ _ __  _ __  _   _",
                "| |    | | | '_ \\| '_ \\| | | |",
                "| |____| | | |_) | |_) | |_| |",
                " \\_____|_|_| .__/| .__/ \\__, |",
                "           | |   | |     __/ |",
                "           |_|   |_|    |___/ ",
                "                              ",
                "                              ",
                "                              "
            };

            var paperclip = new[]
            {
                "      ___",
                "    /    \\",
                "  |     |",
                " ==    ==",
                "(O)   (O)",
                " |     |",
                " | |   | /",
                " | |   ||",
                " | \\__/ |",
                "  \\     |",
                "   \\___/"
            };

            for (var i = 0; i < paperclip.Length; i++)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(clippyText[i]);
                Console.Write("   ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(paperclip[i]);
            }

            Console.ResetColor();
            Console.WriteLine();
        }

        private static void ListenForConsoleHotkeys()
        {
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Spacebar)
                {
                    Console.Clear();
                }
            }
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmClipboardUpdate)
            {
                LogClipboardChange();
                return IntPtr.Zero;
            }

            if (message == WmDestroy)
            {
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
        }

        private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;

            if (monitorWindowHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(monitorWindowHandle);
            }
        }

        private static void LogClipboardChange()
        {
            var changeId = Interlocked.Increment(ref changeCounter);
            var now = DateTime.Now;
            var nowUtc = DateTime.UtcNow;
            var sequenceNumber = NativeMethods.GetClipboardSequenceNumber();
            var ownerWindow = NativeMethods.GetClipboardOwner();
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            var ownerInfo = ResolveWindowProcessInfo(ownerWindow);
            var foregroundInfo = ResolveWindowProcessInfo(foregroundWindow);
            var clipboardSnapshot = ReadClipboardSnapshot();
            var headerColor = changeId % 2 == 0 ? ConsoleColor.Yellow : ConsoleColor.Cyan;

            WriteHeader(string.Format("[{0}] Clipboard changed (Event #{1})", now.ToString("yyyy-MM-dd HH:mm:ss.fff"), changeId), headerColor);
            WriteField("  UTC time", nowUtc.ToString("yyyy-MM-dd HH:mm:ss.fff"), ConsoleColor.DarkGray, ConsoleColor.Gray);
            WriteField("  Sequence number", sequenceNumber.ToString(), ConsoleColor.DarkGray, ConsoleColor.White);
            WriteField("  Monitor thread id", NativeMethods.GetCurrentThreadId().ToString(), ConsoleColor.DarkGray, ConsoleColor.White);
            WriteField("  Clipboard owner HWND", FormatWindowHandle(ownerWindow), ConsoleColor.DarkCyan, ConsoleColor.White);
            WriteField("  Foreground HWND", FormatWindowHandle(foregroundWindow), ConsoleColor.DarkCyan, ConsoleColor.White);
            WriteField("  Clipboard owner", ownerInfo, ConsoleColor.DarkGreen, ConsoleColor.Gray);
            WriteField("  Foreground app", foregroundInfo, ConsoleColor.DarkGreen, ConsoleColor.Gray);
            WriteField("  Open attempts", clipboardSnapshot.OpenAttempts.ToString(), ConsoleColor.DarkYellow, ConsoleColor.White);
            WriteField("  Formats", clipboardSnapshot.Formats, ConsoleColor.Magenta, ConsoleColor.Gray);
            WriteField("  Content", clipboardSnapshot.Summary, ConsoleColor.Blue, ConsoleColor.Gray);

            if (!string.IsNullOrEmpty(clipboardSnapshot.TextSha256))
            {
                WriteField("  Text SHA-256", clipboardSnapshot.TextSha256, ConsoleColor.DarkMagenta, ConsoleColor.White);
            }

            Console.ResetColor();
            Console.WriteLine(new string('=', 100));
            Console.WriteLine();
        }

        private static void WriteHeader(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void WriteField(string label, string value, ConsoleColor labelColor, ConsoleColor valueColor)
        {
            Console.ForegroundColor = labelColor;
            Console.Write(label);
            Console.Write(": ");
            Console.ForegroundColor = valueColor;
            Console.WriteLine(value);
            Console.ResetColor();
        }

        private static string ResolveWindowProcessInfo(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "Unavailable";
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);

            if (processId == 0)
            {
                return "Unavailable";
            }

            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    var title = process.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = "No window title";
                    }

                    var path = string.Empty;
                    try
                    {
                        path = process.MainModule.FileName;
                    }
                    catch
                    {
                    }

                    var started = "Unknown";
                    try
                    {
                        started = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    catch
                    {
                    }

                    return string.Format(
                        "{0} (PID {1}, Session {2}, Started {3}, Responding {4}, Title \"{5}\", Path {6})",
                        process.ProcessName,
                        process.Id,
                        process.SessionId,
                        started,
                        process.Responding,
                        title,
                        string.IsNullOrWhiteSpace(path) ? "Unavailable" : path);
                }
            }
            catch
            {
                return string.Format("PID {0}", processId);
            }
        }

        private static ClipboardSnapshot ReadClipboardSnapshot()
        {
            const int maxRetries = 8;

            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(30);
                    continue;
                }

                try
                {
                    var snapshot = new ClipboardSnapshot();
                    snapshot.OpenAttempts = attempt + 1;
                    snapshot.Formats = ReadClipboardFormats();

                    if (NativeMethods.IsClipboardFormatAvailable(CfUnicodeText))
                    {
                        var handle = NativeMethods.GetClipboardData(CfUnicodeText);
                        var text = ReadUnicodeText(handle);

                        if (text == null)
                        {
                            snapshot.Summary = "Text (unavailable)";
                            return snapshot;
                        }

                        snapshot.TextSha256 = ComputeSha256(text);

                        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
                        if (normalized.Length > 120)
                        {
                            normalized = normalized.Substring(0, 120) + "...";
                        }

                        snapshot.Summary = string.Format("Text ({0} chars): \"{1}\"", text.Length, normalized);
                        return snapshot;
                    }

                    snapshot.Summary = snapshot.Formats == "Empty" ? "Empty" : "Non-text data";
                    return snapshot;
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            return new ClipboardSnapshot
            {
                OpenAttempts = maxRetries,
                Formats = "Unavailable",
                Summary = "Busy (clipboard locked)"
            };
        }

        private static string ReadClipboardFormats()
        {
            var formats = new List<string>();
            uint format = 0;

            while (true)
            {
                format = NativeMethods.EnumClipboardFormats(format);
                if (format == 0)
                {
                    break;
                }

                formats.Add(GetFormatName(format) + "(" + format + ")");
            }

            if (formats.Count == 0)
            {
                return "Empty";
            }

            return string.Join(", ", formats.ToArray());
        }

        private static string ComputeSha256(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                var hash = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);

                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string FormatWindowHandle(IntPtr handle)
        {
            return "0x" + handle.ToInt64().ToString("X");
        }

        private static string ReadUnicodeText(IntPtr clipboardDataHandle)
        {
            if (clipboardDataHandle == IntPtr.Zero)
            {
                return null;
            }

            var data = NativeMethods.GlobalLock(clipboardDataHandle);
            if (data == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(data);
            }
            finally
            {
                NativeMethods.GlobalUnlock(clipboardDataHandle);
            }
        }

        private static string GetFormatName(uint format)
        {
            switch (format)
            {
                case 1:
                    return "CF_TEXT";
                case 2:
                    return "CF_BITMAP";
                case 3:
                    return "CF_METAFILEPICT";
                case 4:
                    return "CF_SYLK";
                case 5:
                    return "CF_DIF";
                case 6:
                    return "CF_TIFF";
                case 7:
                    return "CF_OEMTEXT";
                case 8:
                    return "CF_DIB";
                case 9:
                    return "CF_PALETTE";
                case 10:
                    return "CF_PENDATA";
                case 11:
                    return "CF_RIFF";
                case 12:
                    return "CF_WAVE";
                case 13:
                    return "CF_UNICODETEXT";
                case 15:
                    return "CF_HDROP";
                case 16:
                    return "CF_LOCALE";
                case 17:
                    return "CF_DIBV5";
            }

            var name = new StringBuilder(256);
            if (NativeMethods.GetClipboardFormatName(format, name, name.Capacity) > 0)
            {
                return name.ToString();
            }

            return "Unknown(" + format + ")";
        }
    }

    internal sealed class ClipboardSnapshot
    {
        internal int OpenAttempts { get; set; }

        internal string Formats { get; set; }

        internal string Summary { get; set; }

        internal string TextSha256 { get; set; }
    }

    internal static class NativeMethods
    {
        internal static readonly IntPtr HwndMessage = new IntPtr(-3);

        internal delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClass
        {
            internal uint style;
            internal WindowProc lpfnWndProc;
            internal int cbClsExtra;
            internal int cbWndExtra;
            internal IntPtr hInstance;
            internal IntPtr hIcon;
            internal IntPtr hCursor;
            internal IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Msg
        {
            internal IntPtr hwnd;
            internal uint message;
            internal IntPtr wParam;
            internal IntPtr lParam;
            internal uint time;
            internal int ptX;
            internal int ptY;
            internal uint lPrivate;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClass([In] ref WndClass lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessage([In] ref Msg lpmsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage([In] ref Msg lpMsg);

        [DllImport("user32.dll")]
        internal static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        internal static extern uint EnumClipboardFormats(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}

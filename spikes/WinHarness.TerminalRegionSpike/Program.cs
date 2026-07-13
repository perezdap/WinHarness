using System.Runtime.InteropServices;
using System.Text;
namespace WinHarness.TerminalRegionSpike;

/// <summary>
/// Throws a scroll region (DECSTBM) at a real Windows console and verifies the
/// fixed rows survive scrolling. Run with no args for the interactive demo; pass
/// a file path to run the headless self-verify and write a JSON result there.
/// </summary>
internal static class Program
{
    private const int StdOutputHandle = -11;
    private const short VerifyWidth = 80;
    private const short VerifyHeight = 24;

    private static int Main(string[] args) => args.Length == 1 ? RunVerify(args[0]) : RunInteractive();

    private static int RunInteractive()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Interactive terminal required.");
            return 2;
        }

        int originalBottom;
        try
        {
            originalBottom = Console.WindowHeight;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Could not read terminal dimensions.");
            return 2;
        }

        if (originalBottom < 8)
        {
            Console.Error.WriteLine("Terminal must be at least 8 rows high.");
            return 2;
        }

        int top = 3;
        int bottom = originalBottom - 2;
        Console.CursorVisible = false;
        try
        {
            Console.Write("\x1b[2J\x1b[H");
            WriteFixed(1, "[terminal-region-spike] fixed header");
            WriteFixed(originalBottom - 1, "[terminal-region-spike] fixed footer — press Q to quit");
            SetScrollRegion(top, bottom);
            Console.SetCursorPosition(0, top - 1);

            for (int index = 1; index <= 200; index++)
            {
                while (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                    if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                    {
                        return 0;
                    }
                }

                Console.WriteLine($"scrolling content line {index:000} | {DateTime.Now:HH:mm:ss}");
                Thread.Sleep(75);
            }

            Console.SetCursorPosition(0, bottom - 1);
            Console.WriteLine("Completed. Press any key to restore terminal.");
            Console.ReadKey(intercept: true);
            return 0;
        }
        finally
        {
            SetScrollRegion(1, originalBottom);
            Console.Write("\x1b[2J\x1b[H");
            Console.CursorVisible = true;
        }
    }

    private static int RunVerify(string resultPath)
    {
        IntPtr handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == (IntPtr)(-1))
        {
            WriteResult(resultPath, ok: false, error: "no stdout console handle", rows: null);
            return 2;
        }

        if (!ResizeConsole(handle, VerifyWidth, VerifyHeight))
        {
            WriteResult(resultPath, ok: false, error: "could not resize console (redirected?)", rows: null);
            return 2;
        }

        int height = VerifyHeight;
        int top = 3;
        int bottom = height - 2; // 1-based region rows 3..22 -> 20 scrolling rows
        try
        {
            Console.CursorVisible = false;
            Console.Write("\x1b[2J\x1b[H");
            WriteFixed(1, "[spike] FIXED HEADER — must never scroll away");
            WriteFixed(height, "[spike] FIXED FOOTER — must never scroll away");
            SetScrollRegion(top, bottom);
            Console.SetCursorPosition(0, top - 1);

            // Scroll more lines than the region holds so, if DECSTBM is honored,
            // only the last ~20 survive in the region and the fixed rows are untouched.
            for (int i = 1; i <= 60; i++)
            {
                Console.WriteLine($"scrolling content line {i:000}");
                Thread.Sleep(8);
            }

            Console.Out.Flush();

            if (!ReadGrid(handle, VerifyWidth, (short)height, out string[]? rows) || rows is null)
            {
                WriteResult(resultPath, ok: false, error: "ReadConsoleOutputW failed", rows: null);
                return 2;
            }

            bool headerFixed = rows[0].Contains("FIXED HEADER");
            bool footerFixed = rows[height - 1].Contains("FIXED FOOTER");
            bool scrolled = rows.Any(r => r.Contains("scrolling content line"));
            // The header text must not have leaked into any scrolling-region row.
            bool headerNotInRegion = !rows.Skip(2).Take(height - 3).Any(r => r.Contains("FIXED HEADER"));
            bool ok = headerFixed && footerFixed && headerNotInRegion && scrolled;

            WriteResult(resultPath, ok, error: ok ? null : "fixed rows did not survive scroll", rows: rows);
            return ok ? 0 : 3;
        }
        finally
        {
            SetScrollRegion(1, height);
            Console.Write("\x1b[2J\x1b[H");
            Console.CursorVisible = true;
        }
    }

    private static void WriteResult(string path, bool ok, string? error, string[]? rows)
    {
        StringBuilder json = new();
        json.Append('{');
        json.Append($"\"ok\":{(ok ? "true" : "false")},");
        json.Append($"\"error\":{(error is null ? "null" : $"\"{Escape(error)}\"")},");
        json.Append($"\"width\":{VerifyWidth},");
        json.Append($"\"height\":{VerifyHeight},");
        json.Append("\"rows\":[");
        if (rows is { Length: > 0 })
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                json.Append('\n');
                json.Append($"  \"{Escape(rows[i])}\"");
            }

            json.Append('\n');
        }

        json.Append(']');
        json.Append('}');
        File.WriteAllText(path, json.ToString());
    }

    private static string Escape(string value)
    {
        StringBuilder sb = new(value.Length + 4);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static void SetScrollRegion(int top, int bottom) => Console.Write($"\x1b[{top};{bottom}r");

    private static void WriteFixed(int row, string text) => Console.Write($"\x1b[{row};1H\x1b[2K{text}");

    private static bool ResizeConsole(IntPtr handle, short width, short height)
    {
        // Shrink the window to 1x1 first so reducing the buffer size is permitted,
        // then size the buffer, then open the window back up to the full buffer.
        SmallRect tiny = new() { Left = 0, Top = 0, Right = 0, Bottom = 0 };
        if (!SetConsoleWindowInfo(handle, absolute: true, ref tiny))
        {
            return false;
        }

        if (!SetConsoleScreenBufferSize(handle, new Coord { X = width, Y = height }))
        {
            return false;
        }

        SmallRect full = new() { Left = 0, Top = 0, Right = (short)(width - 1), Bottom = (short)(height - 1) };
        return SetConsoleWindowInfo(handle, absolute: true, ref full);
    }

    private static bool ReadGrid(IntPtr handle, short width, short height, out string[]? rows)
    {
        rows = null;
        CharInfo[] buffer = new CharInfo[width * height];
        SmallRect region = new() { Left = 0, Top = 0, Right = (short)(width - 1), Bottom = (short)(height - 1) };
        if (!ReadConsoleOutputW(handle, buffer, new Coord { X = width, Y = height }, new Coord { X = 0, Y = 0 }, ref region))
        {
            return false;
        }

        StringBuilder sb = new(width);
        string[] result = new string[height];
        for (int y = 0; y < height; y++)
        {
            sb.Clear();
            for (int x = 0; x < width; x++)
            {
                char c = (char)buffer[(y * width) + x].Char;
                sb.Append(c == '\0' ? ' ' : c);
            }

            result[y] = sb.ToString().TrimEnd();
        }

        rows = result;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, Coord dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleWindowInfo(IntPtr hConsoleOutput, [MarshalAs(UnmanagedType.Bool)] bool absolute, ref SmallRect lpConsoleWindow);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputW(IntPtr hConsoleOutput, [Out] CharInfo[] lpBuffer, Coord dwBufferSize, Coord dwBufferCoord, ref SmallRect lpReadRegion);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CharInfo
    {
        public ushort Char;
        public ushort Attributes;
    }
}

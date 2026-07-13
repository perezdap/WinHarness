using System.Runtime.InteropServices;

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

static void SetScrollRegion(int top, int bottom)
{
    Console.Write($"\x1b[{top};{bottom}r");
}

static void WriteFixed(int row, string text)
{
    Console.Write($"\x1b[{row};1H\x1b[2K{text}");
}

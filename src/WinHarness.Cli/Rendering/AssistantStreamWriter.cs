using System.Globalization;
using Spectre.Console;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Streams raw assistant tokens to the console as they arrive while tracking the
/// on-screen rows used, so the streamed block can be erased and re-rendered as
/// markdown when the turn completes. Erase is only attempted when the output is
/// interactive, the terminal size is known, and the block did not scroll.
/// </summary>
internal sealed class AssistantStreamWriter
{
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _canMeasure;

    private bool _labelWritten;
    private int _column;
    private int _rows;

    public AssistantStreamWriter()
    {
        try
        {
            _width = Console.WindowWidth;
            _height = Console.WindowHeight;
            _canMeasure = _width > 0 && _height > 0;
        }
        catch (IOException)
        {
            _canMeasure = false;
        }
    }

    public bool HasOutput => _labelWritten;

    public void Write(string text)
    {
        if (!_labelWritten)
        {
            AnsiConsole.Markup("[bold blue]•[/] ");
            _labelWritten = true;
            _column = 2;
        }

        Console.Write(text);
        Track(text);
    }

    public bool TryEraseForReRender()
    {
        if (!_interactive || !_canMeasure || !_labelWritten)
        {
            return false;
        }

        // If the streamed block is taller than the window it has scrolled and the
        // saved relative position is no longer reliable; leave the raw text.
        if (_rows + 1 > _height)
        {
            return false;
        }

        if (_rows > 0)
        {
            Console.Write($"\x1b[{_rows.ToString(CultureInfo.InvariantCulture)}A");
        }

        Console.Write('\r');
        Console.Write("\x1b[0J");
        return true;
    }

    private void Track(string text)
    {
        foreach (char character in text)
        {
            if (character == '\n')
            {
                _rows++;
                _column = 0;
            }
            else if (character == '\r')
            {
                _column = 0;
            }
            else
            {
                _column++;
                if (_canMeasure && _column >= _width)
                {
                    _rows++;
                    _column = 0;
                }
            }
        }
    }
}

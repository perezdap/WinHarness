using System.Text;
using Spectre.Console;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Wraps Spectre.Console prompts so <c>Esc</c> (and best-effort <c>Ctrl+C</c>)
/// cancels the picker instead of trapping the user. Spectre 0.57's
/// <see cref="SelectionPrompt{T}"/> fires its <see cref="SelectionPrompt{T}.CancelResult"/>
/// on <c>Esc</c>; we set that to <c>null</c> so a cancelled picker returns
/// <c>null</c> (the choices are always non-null reference types).
/// <para>
/// <c>Ctrl+C</c> is harder: Spectre's key read is a blocking
/// <see cref="System.Console.ReadKey"/> that ignores the cancellation token, so
/// token cancellation is best-effort (it unblocks the next poll when the console
/// is in a polling mode). We still register a <see cref="System.Console.CancelKeyPress"/>
/// handler that swallows the break (so the process survives) and cancels the
/// token; <c>Esc</c> remains the reliable escape and is what the prompt titles
/// advertise.
/// </para>
/// </summary>
internal static class InteractivePicker
{
    /// <summary>
    /// Shows a <see cref="SelectionPrompt{T}"/> with Esc-to-cancel. Returns the
    /// selection, or <c>null</c> when the user cancelled (Esc / Ctrl+C).
    /// </summary>
    public static async ValueTask<T?> ShowAsync<T>(SelectionPrompt<T> prompt, string cancelledMessage = "cancelled.")
        where T : class
    {
        // Esc -> Spectre returns this; null is the cancel sentinel (choices are non-null).
        prompt.CancelResult = () => null!;

        using CancellationTokenSource cts = new();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            // Swallow Ctrl+C so the process survives, and signal the prompt.
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await prompt.ShowAsync(AnsiConsole.Console, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(cancelledMessage)}[/]");
            return null;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>
    /// Prompts for a single line of text with Esc/Ctrl+C-to-cancel. Returns the
    /// trimmed input, or <c>null</c> when the user cancelled.
    /// </summary>
    public static ValueTask<string?> PromptTextAsync(string title, string cancelledMessage = "cancelled.")
    {
        AnsiConsole.Markup($"[bold]{Markup.Escape(title)}[/] [grey](Esc/Ctrl+C to cancel)[/]: ");
        if (Console.IsInputRedirected)
        {
            return new(Console.ReadLine()?.Trim());
        }

        StringBuilder buffer = new();
        bool previousTreatControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    return new((string?)null);
                }

                if (key.Key == ConsoleKey.Escape || IsCtrlC(key))
                {
                    Console.WriteLine();
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(cancelledMessage)}[/]");
                    return new((string?)null);
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return new(buffer.ToString().Trim());
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }

                    continue;
                }

                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = previousTreatControlC;
        }
    }

    /// <summary>
    /// Shows a yes/no confirmation with Esc/Ctrl+C-to-cancel. Returns
    /// <c>true</c> for yes, <c>false</c> for no, <c>null</c> when cancelled.
    /// </summary>
    public static async ValueTask<bool?> ConfirmAsync(string title, bool defaultValue = false)
    {
        SelectionPrompt<string> prompt = new()
        {
            Title = $"{title} (Esc to cancel)"
        };
        prompt.PageSize(5)
              .AddChoices("yes", "no");
        // Put the default first so it's highlighted initially.
        if (defaultValue)
        {
            prompt.DefaultValue("yes");
        }

        string? selected = await ShowAsync(prompt, "cancelled.").ConfigureAwait(false);
        return selected switch
        {
            "yes" => true,
            "no" => false,
            _ => null
        };
    }

    private static bool IsCtrlC(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0;
}

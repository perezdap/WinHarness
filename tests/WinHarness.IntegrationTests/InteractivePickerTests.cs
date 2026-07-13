using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;
using Spectre.Console.Testing;
using WinHarness.Cli.Chat;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class InteractivePickerTests
{
    private static TestConsole NewInteractiveConsole()
    {
        TestConsole console = new TestConsole().Width(80);
        // SelectionPrompt.Show refuses to run unless the console advertises
        // interactive capabilities; flip it on (the real terminal does this).
        typeof(Capabilities).GetProperty(nameof(Capabilities.Interactive))!.SetValue(
            console.Profile.Capabilities, true);
        return console;
    }

    [TestMethod]
    public async Task Esc_ReturnsNull_Cancelled()
    {
        TestConsole console = NewInteractiveConsole();
        console.Input.PushKey(ConsoleKey.Escape);
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            SelectionPrompt<string> prompt = new SelectionPrompt<string>()
                .Title("Pick (Esc to cancel)")
                .AddChoices("one", "two", "three");
            string? result = await InteractivePicker.ShowAsync(prompt, "cancelled.").ConfigureAwait(false);
            Assert.IsNull(result, "Esc should cancel the picker and return null.");
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    [TestMethod]
    public async Task Enter_ReturnsSelected_NotCancelled()
    {
        TestConsole console = NewInteractiveConsole();
        // First choice is highlighted by default; Enter submits it.
        console.Input.PushKey(ConsoleKey.Enter);
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            SelectionPrompt<string> prompt = new SelectionPrompt<string>()
                .Title("Pick")
                .AddChoices("one", "two", "three");
            string? result = await InteractivePicker.ShowAsync(prompt).ConfigureAwait(false);
            Assert.AreEqual("one", result);
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    [TestMethod]
    public async Task ConfirmAsync_Yes_ReturnsTrue()
    {
        TestConsole console = NewInteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter); // "yes" is the first/default-when-highlighted choice
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            bool? result = await InteractivePicker.ConfirmAsync("Delete?").ConfigureAwait(false);
            Assert.AreEqual(true, result);
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    [TestMethod]
    public async Task ConfirmAsync_Esc_ReturnsNull()
    {
        TestConsole console = NewInteractiveConsole();
        console.Input.PushKey(ConsoleKey.Escape);
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            bool? result = await InteractivePicker.ConfirmAsync("Delete?").ConfigureAwait(false);
            Assert.IsNull(result, "Esc should cancel the confirmation.");
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }
}

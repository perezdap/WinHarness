using Terminal.Gui.Drawing;

using Attribute = Terminal.Gui.Drawing.Attribute;

namespace WinHarness.Cli.Tui;

/// <summary>Pi-inspired dark palette mapped to Terminal.Gui's 16-color model.</summary>
internal static class TuiTheme
{
    public static readonly Attribute DefaultText = new(Color.White, Color.Black);
    public static readonly Attribute DimText = new(Color.Gray, Color.Black);
    public static readonly Attribute AccentText = new(Color.BrightCyan, Color.Black);
    public static readonly Attribute RoleLabel = new(Color.BrightBlue, Color.Black);
    public static readonly Attribute UserText = new(Color.White, Color.DarkGray);
    public static readonly Attribute UserRoleLabel = new(Color.BrightCyan, Color.DarkGray);
    public static readonly Attribute SystemText = new(Color.BrightGreen, Color.Black);
    public static readonly Attribute SystemRoleLabel = new(Color.BrightGreen, Color.Black);
    public static readonly Attribute AssistantRoleLabel = new(Color.BrightBlue, Color.Black);
    public static readonly Attribute StatusText = new(Color.BrightCyan, Color.Black);
    public static readonly Attribute Border = new(Color.BrightBlue, Color.Black);
    public static readonly Attribute InputText = new(Color.White, Color.Black);
    public static readonly Attribute InputFocus = new Attribute(Color.Black, Color.DarkGray);
    public static readonly Attribute ToolText = new(Color.Yellow, Color.Black);

    public static Attribute RoleLabelFor(TranscriptRole role) =>
        role switch
        {
            TranscriptRole.User => UserRoleLabel,
            TranscriptRole.Assistant => AssistantRoleLabel,
            TranscriptRole.System => SystemRoleLabel,
            _ => DimText
        };

    public static Attribute ContentFor(TranscriptRole role) =>
        role switch
        {
            TranscriptRole.User => UserText,
            TranscriptRole.Assistant => DefaultText,
            TranscriptRole.System => SystemText,
            _ => DefaultText
        };
}

internal enum TranscriptRole
{
    System,
    User,
    Assistant
}

internal enum TranscriptRowKind
{
    Spacer,
    RoleLabel,
    Content
}

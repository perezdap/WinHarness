using Terminal.Gui.Drawing;

using Attribute = Terminal.Gui.Drawing.Attribute;

namespace WinHarness.Cli.Rendering;

internal static class MarkdownTuiStyles
{
    public static Attribute ForBlock(MarkdownBlockStyle blockStyle, Attribute baseAttr)
    {
        return blockStyle switch
        {
            MarkdownBlockStyle.Heading1 => new Attribute(Color.White, baseAttr.Background),
            MarkdownBlockStyle.Heading2 => new Attribute(Color.BrightCyan, baseAttr.Background),
            MarkdownBlockStyle.Heading3 => new Attribute(Color.BrightCyan, baseAttr.Background),
            MarkdownBlockStyle.Heading4 => new Attribute(Color.White, baseAttr.Background),
            MarkdownBlockStyle.BlockQuote => new Attribute(Color.Gray, baseAttr.Background),
            MarkdownBlockStyle.CodeFence => new Attribute(Color.Gray, Color.DarkGray),
            MarkdownBlockStyle.HorizontalRule => new Attribute(Color.Gray, baseAttr.Background),
            _ => baseAttr
        };
    }

    public static Attribute ForEmphasis(MarkdownEmphasis emphasis, Attribute baseAttr)
    {
        return emphasis switch
        {
            MarkdownEmphasis.Bold => new Attribute(Color.White, baseAttr.Background),
            MarkdownEmphasis.Italic => new Attribute(Color.Gray, baseAttr.Background),
            MarkdownEmphasis.InlineCode => new Attribute(Color.Black, Color.Gray),
            MarkdownEmphasis.Link => new Attribute(Color.BrightBlue, baseAttr.Background),
            MarkdownEmphasis.Strikethrough => new Attribute(Color.Gray, baseAttr.Background),
            MarkdownEmphasis.ListMarker => new Attribute(Color.BrightYellow, baseAttr.Background),
            _ => baseAttr
        };
    }
}

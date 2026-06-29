using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

using Attribute = Terminal.Gui.Drawing.Attribute;

namespace WinHarness.Cli.Tui;

/// <summary>
/// Scrollable chat transcript built from Terminal.Gui <see cref="Markdown"/> views and role labels.
/// </summary>
internal sealed class TranscriptPanel : View
{
    private const int ContentIndent = 2;
    private readonly View _content;
    private Markdown? _activeMarkdown;
    private View? _tailView;
    private int _contentWidth = 80;

    public TranscriptPanel()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        TabStop = TabBehavior.TabStop;
        ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        MouseBindings.Add(MouseFlags.WheeledUp, Command.ScrollUp);
        MouseBindings.Add(MouseFlags.WheeledDown, Command.ScrollDown);

        _content = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Auto(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        Add(_content);
    }

    public void SetContentWidth(int width)
    {
        _contentWidth = Math.Max(24, width);
    }

    public void Rebuild(IReadOnlyList<TranscriptMessage> messages, bool renderMarkdown)
    {
        _activeMarkdown = null;
        _tailView = null;
        _content.RemoveAll();

        bool addedAny = false;
        for (int index = 0; index < messages.Count; index++)
        {
            TranscriptMessage message = messages[index];
            if (!HasDisplayableContent(message))
            {
                continue;
            }

            if (addedAny)
            {
                AddSpacer();
            }

            addedAny = true;
            AddRoleLabel(message.Role);
            if (message.Role == TranscriptRole.Assistant && renderMarkdown)
            {
                Markdown markdown = AddAssistantMarkdown(message.Text);
                if (index == messages.Count - 1)
                {
                    _activeMarkdown = markdown;
                }
            }
            else
            {
                AddPlainContent(message.Role, message.Text);
            }
        }

        FinishLayout();
    }

    public void UpdateActiveAssistant(string text, bool renderMarkdown)
    {
        if (!renderMarkdown || _activeMarkdown is null)
        {
            return;
        }

        _activeMarkdown.Text = text;
        RefreshStreamingLayout();
    }

    public void FinalizeActiveAssistantLayout()
    {
        if (_activeMarkdown is null)
        {
            return;
        }

        FinishLayout();
    }

    public void ClearActiveAssistant()
    {
        _activeMarkdown = null;
    }

    private void AddSpacer()
    {
        Label spacer = CreateLabel(" ", TuiTheme.DefaultText);
        PlaceBelow(spacer);
        _content.Add(spacer);
        _tailView = spacer;
    }

    private void AddRoleLabel(TranscriptRole role)
    {
        string label = role switch
        {
            TranscriptRole.User => "you",
            TranscriptRole.Assistant => "assistant",
            TranscriptRole.System => "system",
            _ => "system"
        };

        Label roleView = CreateLabel(label, TuiTheme.RoleLabelFor(role));
        PlaceBelow(roleView);
        _content.Add(roleView);
        _tailView = roleView;
    }

    private void AddPlainContent(TranscriptRole role, string text)
    {
        Attribute contentAttr = TuiTheme.ContentFor(role);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int contentWidth = Math.Max(1, _contentWidth - ContentIndent);

        bool any = false;
        foreach (string chunk in WordWrap(normalized, contentWidth))
        {
            any = true;
            Label line = CreateLabel(new string(' ', ContentIndent) + chunk, contentAttr);
            PlaceBelow(line);
            _content.Add(line);
            _tailView = line;
        }

        if (!any)
        {
            Label empty = CreateLabel(new string(' ', ContentIndent), contentAttr);
            PlaceBelow(empty);
            _content.Add(empty);
            _tailView = empty;
        }
    }

    private Markdown AddAssistantMarkdown(string markdown)
    {
        Markdown view = new()
        {
            Text = markdown,
            X = ContentIndent,
            Width = Dim.Fill()! - ContentIndent,
            Height = Dim.Auto(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            ShowHeadingPrefix = false,
            ShowCopyButtons = false
        };
        PlaceBelow(view);
        _content.Add(view);
        _tailView = view;
        return view;
    }

    private Label CreateLabel(string text, Attribute attribute)
    {
        Label label = new()
        {
            Text = text,
            X = 0,
            Width = Dim.Fill(),
            Height = Dim.Auto(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        label.SetScheme(new Scheme { Normal = attribute });
        return label;
    }

    private void PlaceBelow(View view)
    {
        view.Y = _tailView is null ? Pos.Absolute(0) : Pos.Bottom(_tailView);
    }

    private void FinishLayout()
    {
        RefreshContentLayout(scrollToEnd: true);
    }

    private void RefreshStreamingLayout()
    {
        RefreshContentLayout(scrollToEnd: true);
    }

    private void RefreshContentLayout(bool scrollToEnd)
    {
        LayoutContentChildren();
        SyncContentSize();
        SetNeedsLayout();
        Layout();
        if (scrollToEnd)
        {
            ScrollToEnd();
        }

        SetNeedsDraw();
    }

    private void LayoutContentChildren()
    {
        int width = Math.Max(1, Viewport.Width);
        _content.SetNeedsLayout();
        _content.Layout(new Size(width, int.MaxValue));
    }

    private void SyncContentSize()
    {
        int width = Math.Max(1, Viewport.Width);
        int height = Math.Max(MeasureContentHeight(), Viewport.Height);
        SetContentSize(new Size(width, height));
    }

    private int MeasureContentHeight()
    {
        if (_tailView is null)
        {
            return 1;
        }

        return Math.Max(1, _tailView.Frame.Bottom);
    }

    private void ScrollToEnd()
    {
        int contentHeight = GetContentHeight();
        int viewportHeight = Viewport.Height;
        if (contentHeight <= viewportHeight)
        {
            ScrollVertical(0);
            return;
        }

        ScrollVertical(contentHeight - viewportHeight);
    }

    internal static bool HasDisplayableContent(TranscriptMessage message)
        => !string.IsNullOrWhiteSpace(message.Text);

    private static IEnumerable<string> WordWrap(string text, int width)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        string remaining = text;
        while (remaining.Length > 0)
        {
            if (remaining.Length <= width)
            {
                yield return remaining;
                yield break;
            }

            int breakAt = width;
            ReadOnlySpan<char> slice = remaining.AsSpan(0, width);
            int lastNewline = slice.LastIndexOf('\n');
            int lastSpace = slice.LastIndexOf(' ');
            if (lastNewline >= 0)
            {
                breakAt = lastNewline + 1;
            }
            else if (lastSpace > width / 3)
            {
                breakAt = lastSpace;
            }

            yield return remaining[..breakAt].TrimEnd('\n');
            remaining = remaining[breakAt..].TrimStart('\n').TrimStart(' ');
        }
    }
}

/// <summary>A logical transcript message before display.</summary>
internal sealed record TranscriptMessage(TranscriptRole Role, string Text);

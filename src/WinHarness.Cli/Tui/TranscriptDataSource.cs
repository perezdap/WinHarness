using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WinHarness.Cli.Rendering;

using Attribute = Terminal.Gui.Drawing.Attribute;

namespace WinHarness.Cli.Tui;

/// <summary>
/// Virtualized transcript renderer: only visible rows are painted, with markdown emphasis spans.
/// </summary>
internal sealed class TranscriptDataSource : IListDataSource, IDisposable
{
    private readonly ObservableCollection<TranscriptRow> _rows;
    private bool _disposed;

    public TranscriptDataSource(ObservableCollection<TranscriptRow> rows)
    {
        _rows = rows;
    }

    public int Count => _rows.Count;

    public int MaxItemLength => _rows.Count == 0 ? 0 : _rows.Max(static r => r.Text.Length);

    public bool SuspendCollectionChangedEvent { get; set; }

    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => _rows.CollectionChanged += value;
        remove => _rows.CollectionChanged -= value;
    }

    public bool IsMarked(int item) => false;

    public void SetMark(int item, bool value)
    {
    }

    public IList ToList() => _rows.ToList();

    public void Render(ListView container, bool selected, int item, int col, int line, int width, int start)
    {
        if ((uint)item >= (uint)_rows.Count)
        {
            return;
        }

        TranscriptRow row = _rows[item];
        container.Move(col, line);

        Attribute baseAttr = row.Kind switch
        {
            TranscriptRowKind.Spacer => TuiTheme.DefaultText,
            TranscriptRowKind.RoleLabel => TuiTheme.RoleLabelFor(row.Role),
            _ => TuiTheme.ContentFor(row.Role)
        };

        if (row.Kind == TranscriptRowKind.Content && row.BlockStyle != MarkdownBlockStyle.None)
        {
            baseAttr = MarkdownTuiStyles.ForBlock(row.BlockStyle, baseAttr);
        }

        string text = row.Text;
        if (text.Length > width)
        {
            text = text[..width];
        }

        if (row.Kind == TranscriptRowKind.Content && row.Runs is { Count: > 0 })
        {
            RenderStyledText(container, text, row.Runs, baseAttr);
        }
        else
        {
            container.SetAttribute(baseAttr);
            container.AddStr(text);
        }

        int remaining = width - text.Length;
        if (remaining > 0)
        {
            container.SetAttribute(baseAttr);
            container.AddStr(new string(' ', remaining));
        }
    }

    private static void RenderStyledText(
        ListView container,
        string text,
        IReadOnlyList<MarkdownRun> runs,
        Attribute baseAttr)
    {
        int pos = 0;
        foreach (MarkdownRun run in runs.OrderBy(static r => r.Start))
        {
            if (run.Start > text.Length)
            {
                break;
            }

            int runStart = run.Start;
            int runLength = Math.Min(run.Length, text.Length - runStart);
            if (runLength <= 0)
            {
                continue;
            }

            if (runStart > pos)
            {
                container.SetAttribute(baseAttr);
                container.AddStr(text[pos..runStart]);
            }

            container.SetAttribute(MarkdownTuiStyles.ForEmphasis(run.Emphasis, baseAttr));
            container.AddStr(text[runStart..(runStart + runLength)]);
            pos = runStart + runLength;
        }

        if (pos < text.Length)
        {
            container.SetAttribute(baseAttr);
            container.AddStr(text[pos..]);
        }
    }

    public bool RenderMark(ListView container, int item, int col, int line, bool marked, bool selected)
        => false;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}

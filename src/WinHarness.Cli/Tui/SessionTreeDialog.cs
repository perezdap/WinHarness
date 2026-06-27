using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WinHarness.Cli.Chat;
using WinHarness.Sessions;

namespace WinHarness.Cli.Tui;

/// <summary>
/// Terminal.Gui modal for <c>/tree</c> branch navigation.
/// </summary>
internal sealed class SessionTreeDialog : Dialog<IReadOnlyList<string>>
{
    private readonly ISessionManager _sessionManager;
    private readonly Action<string> _onBranch;
    private readonly IReadOnlyList<SessionTreeChoices.Choice> _choices;
    private readonly ListView _listView;

    private SessionTreeDialog(ISessionManager sessionManager, Action<string> onBranch)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(onBranch);

        _sessionManager = sessionManager;
        _onBranch = onBranch;
        _choices = SessionTreeChoices.BuildChoices(sessionManager);

        Title = "Session tree";
        Width = Dim.Percent(85);
        Height = Dim.Percent(75);

        ObservableCollection<string> labels = new(_choices.Select(SessionTreeChoices.FormatListLabel));
        _listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };
        _listView.SetSource(labels);
        _listView.Accepting += OnListAccepting;
        Add(_listView);
        AddButton(new Button { Text = "_Cancel" });
    }

    /// <summary>
    /// Runs the modal on the UI thread and returns slash-command feedback lines.
    /// </summary>
    public static IReadOnlyList<string> Show(
        IApplication app,
        ISessionManager sessionManager,
        Action<string> onBranch)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(onBranch);

        if (SessionTreeChoices.BuildChoices(sessionManager).Count == 0)
        {
            return ["Session tree is empty. Send a message first."];
        }

        using SessionTreeDialog dialog = new(sessionManager, onBranch);
        app.Run(dialog);
        return dialog.Result ?? ["Branching cancelled."];
    }

    private void OnListAccepting(object? sender, CommandEventArgs args)
    {
        args.Handled = true;
        BranchSelected();
    }

    protected override bool OnAccepting(CommandEventArgs args)
    {
        if (base.OnAccepting(args))
        {
            return true;
        }

        Result = ["Branching cancelled."];
        RequestStop();
        return false;
    }

    private void BranchSelected()
    {
        if (_listView.SelectedItem is not int index || (uint)index >= (uint)_choices.Count)
        {
            return;
        }

        Result = SessionTreeChoices.ApplyBranch(_sessionManager, _choices[index].Entry, _onBranch);
        RequestStop();
    }
}
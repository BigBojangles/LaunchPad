using System.Collections.Specialized;
using Avalonia.VisualTree;

namespace LaunchPad.Views;

public partial class HomeView : UserControl
{
    private readonly MainWindow _owner;
    private SessionBoard? _board;
    private Border? _dropBorder;

    public HomeView(MainWindow owner, bool nativeOnly = false)
    {
        _owner = owner;
        InitializeComponent();
        MachineButton.IsVisible = MachineSeparator.IsVisible = !nativeOnly;
        DataContextChanged += (_, _) => WatchBoard(DataContext as SessionBoard);
        TileScroll.SizeChanged += (_, _) => _board?.FitTiles(TileScroll.Bounds.Width);
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropBorder(null));
    }

    private void WatchBoard(SessionBoard? board)
    {
        if (_board is not null)
            _board.AllSessions.CollectionChanged -= Sessions_Changed;
        _board = board;
        if (_board is not null)
            _board.AllSessions.CollectionChanged += Sessions_Changed;
        Sessions_Changed(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void Sessions_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EmptyHint.IsVisible = !(_board is { AllSessions.Count: > 0 });
        _board?.FitTiles(TileScroll.Bounds.Width);
    }

    private static string? DropProject(object? source)
    {
        if (source is not Visual visual) return null;
        var controls = visual.GetVisualAncestors().OfType<Control>().Prepend(visual as Control).Where(c => c is not null);
        return controls.Select(c => c!.DataContext).OfType<FolderGroup>().FirstOrDefault(g => g.ProjectPath is not null)?.ProjectPath
            ?? controls.Select(c => c!.DataContext).OfType<SessionItem>().FirstOrDefault()?.ProjectPath;
    }
    private void SetDropBorder(Border? border)
    {
        _dropBorder?.Classes.Remove("groupDrop");
        _dropBorder = border;
        _dropBorder?.Classes.Add("groupDrop");
    }
    private string? DropTarget(DragEventArgs e)
    {
        if (DropProject(e.Source) is { } project) return project;
        // Blank board space restores the tile to its real project's display group.
        if (!new Rect(TileScroll.Bounds.Size).Contains(e.GetPosition(TileScroll))) return null;
        var id = e.DataTransfer.TryGetValue(SessionMark.DragSessionFormat);
        return _board?.AllSessions.FirstOrDefault(item => item.Id == id)?.ProjectPath;
    }
    private void DragOver(object? sender, DragEventArgs e)
    {
        var valid = e.DataTransfer.TryGetValue(SessionMark.DragSessionFormat) is string && DropTarget(e) is not null;
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        SetDropBorder(valid && e.Source is Visual v ? v.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.DataContext is FolderGroup) : null);
        e.Handled = true;
    }
    private void Drop(object? sender, DragEventArgs e)
    {
        SetDropBorder(null);
        if (e.DataTransfer.TryGetValue(SessionMark.DragSessionFormat) is string id && DropTarget(e) is { } project)
        {
            try { _board?.MoveToGroup(id, project); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { _ = UiDialogs.ShowAsync(_owner, error.Message); }
        }
        e.Handled = true;
    }

    private async void Onboarding_Click(object sender, RoutedEventArgs e) => await _owner.ShowOnboarding();

    private async void Share_Click(object sender, RoutedEventArgs e) => await _owner.ShowShare();

    private async void Machine_Click(object sender, RoutedEventArgs e) => await _owner.ShowMachine();
    private void GroupName_Saved(object? sender, DisplayNameSavedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FolderGroup { ProjectPath: { } path }) _owner.SaveProjectDisplayName(path, e.Name, e.Reset);
    }
}

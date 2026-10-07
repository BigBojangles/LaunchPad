using System.Collections.Specialized;

namespace LaunchPad.Views;

public partial class HomeView : UserControl
{
    private readonly MainWindow _owner;
    private SessionBoard? _board;

    public HomeView(MainWindow owner, bool nativeOnly = false)
    {
        _owner = owner;
        InitializeComponent();
        MachineButton.IsVisible = MachineSeparator.IsVisible = !nativeOnly;
        DataContextChanged += (_, _) => WatchBoard(DataContext as SessionBoard);
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
    }

    private async void Onboarding_Click(object sender, RoutedEventArgs e) => await _owner.ShowOnboarding();

    private async void Share_Click(object sender, RoutedEventArgs e) => await _owner.ShowShare();

    private async void Machine_Click(object sender, RoutedEventArgs e) => await _owner.ShowMachine();
    private void GroupName_Saved(object? sender, DisplayNameSavedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FolderGroup { ProjectPath: { } path }) _owner.SaveProjectDisplayName(path, e.Name, e.Reset);
    }
}

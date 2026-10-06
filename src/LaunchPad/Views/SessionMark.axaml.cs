using Avalonia.VisualTree;

namespace LaunchPad.Views;

public partial class SessionMark : UserControl
{
    public static readonly StyledProperty<bool> ShowCaptionProperty =
        AvaloniaProperty.Register<SessionMark, bool>(nameof(ShowCaption), defaultValue: true);

    public SessionMark()
    {
        InitializeComponent();
        PropertyChanged += (_, e) => { if (e.Property == ShowCaptionProperty) Caption.IsVisible = StatusLabel.IsVisible = ShowCaption; };
        Loaded += (_, _) => Caption.IsVisible = StatusLabel.IsVisible = ShowCaption;
        Loaded += (_, _) => SetMenu();
        Focusable = true;
        KeyDown += (_, e) =>
        {
            if (IsChildControl(e.Source)) return;
            if (e.Key is Key.Enter or Key.Space) { SelectSession(); e.Handled = true; }
        };
    }

    public bool ShowCaption
    {
        get => GetValue(ShowCaptionProperty);
        set => SetValue(ShowCaptionProperty, value);
    }

    private void OnDown(object sender, PointerPressedEventArgs e)
    {
        if (IsChildControl(e.Source)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { Focus(); e.Handled = true; }
    }

    private void OnUp(object sender, PointerReleasedEventArgs e)
    {
        if (IsChildControl(e.Source)) return;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        SelectSession();
    }

    private void SelectSession()
    {
        if (DataContext is not SessionItem item) return;
        var board = this.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<SessionBoard>().FirstOrDefault();
        board?.Activate(item);
    }

    private bool IsChildControl(object? source) => source is Visual visual &&
        (ReferenceEquals(visual, Caption) || ReferenceEquals(visual, MenuButton) ||
         visual.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, Caption) || ReferenceEquals(ancestor, MenuButton)));

    private SessionBoard? Board() => this.GetVisualAncestors().OfType<Control>().Select(control => control.DataContext).OfType<SessionBoard>().FirstOrDefault();
    private void Name_Saved(object? sender, DisplayNameSavedEventArgs e)
    {
        if (DataContext is SessionItem item) Board()?.RenameSession?.Invoke(item, e.Name, e.Reset);
    }
    private void SetMenu()
    {
        if (DataContext is SessionItem item) ContextMenu = Board()?.ProjectMenu?.Invoke(item.ProjectPath);
    }
    private void Menu_Requested(object? sender, ContextRequestedEventArgs e) { SetMenu(); }
    private void Menu_Click(object? sender, RoutedEventArgs e) { e.Handled = true; SetMenu(); ContextMenu?.Open(this); }
}

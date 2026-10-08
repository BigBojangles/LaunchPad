using Avalonia.VisualTree;

namespace LaunchPad.Views;

public partial class SessionMark : UserControl
{
    public static readonly DataFormat<string> DragSessionFormat = DataFormat.CreateStringApplicationFormat("LaunchPad.SessionId");
    public static readonly StyledProperty<double> TileWidthProperty = AvaloniaProperty.Register<SessionMark, double>(nameof(TileWidth), 76);
    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<SessionMark, double>(nameof(IconSize), 40);
    public static readonly StyledProperty<double> IconFrameSizeProperty = AvaloniaProperty.Register<SessionMark, double>(nameof(IconFrameSize), 48);
    public double TileWidth { get => GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public double IconFrameSize => GetValue(IconFrameSizeProperty);
    private Point? _pressed;
    private bool _suppressRelease;
    public static readonly StyledProperty<bool> ShowCaptionProperty =
        AvaloniaProperty.Register<SessionMark, bool>(nameof(ShowCaption), defaultValue: true);

    public SessionMark()
    {
        InitializeComponent();
        Width = TileWidth;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == TileWidthProperty) Width = TileWidth;
            if (e.Property == IconSizeProperty) SetValue(IconFrameSizeProperty, IconSize + 8);
        };
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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = e.GetPosition(this); _suppressRelease = false; Focus(); e.Handled = true; }
    }

    private async void OnMove(object sender, PointerEventArgs e)
    {
        if (!ShowCaption || _pressed is not { } start || DataContext is not SessionItem item || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var distance = e.GetPosition(this) - start;
        if (Math.Abs(distance.X) < 6 && Math.Abs(distance.Y) < 6) return;
        _pressed = null;
        _suppressRelease = true;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DragSessionFormat, item.Id));
        try { await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move); }
        catch (InvalidOperationException) { }
    }

    private void OnUp(object sender, PointerReleasedEventArgs e)
    {
        if (IsChildControl(e.Source)) return;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        _pressed = null;
        if (_suppressRelease) { _suppressRelease = false; return; }
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

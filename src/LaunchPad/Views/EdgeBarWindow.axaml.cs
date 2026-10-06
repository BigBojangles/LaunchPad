using Avalonia.Animation;

namespace LaunchPad.Views;

public partial class EdgeBarWindow : Window
{
    private const double ParkedWidth = 10;
    private const double ParkedHeight = 160;
    private bool _popped, _pinned, _wanted;
    private Window? _main;

    public EdgeBarWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => { if (_popped && IsVisible) Center(); };
    }

    public void SyncToMain(Window main)
    {
        _main = main;
        _wanted = main.WindowState == WindowState.Minimized || IsOutOfFrame(main);
        Apply();
    }

    public void ShowPinned()
    {
        _pinned = true;
        UpdatePin();
        Apply();
    }

    private static bool IsOutOfFrame(Window main)
    {
        if (main.WindowState != WindowState.Normal || !main.IsVisible) return false;
        var rect = new PixelRect(main.Position, PixelSize.FromSize(main.Bounds.Size, main.RenderScaling));
        var visibleArea = main.Screens.All.Sum(s =>
        {
            var overlap = rect.Intersect(s.Bounds);
            return (double)overlap.Width * overlap.Height;
        });
        return rect.Width > 0 && rect.Height > 0 && visibleArea < 0.25 * rect.Width * rect.Height;
    }

    private void Apply()
    {
        if (_pinned) { if (!IsVisible) Show(); Pop(false); }
        else if (_wanted) { if (!IsVisible) Show(); if (!_popped) CollapseRow(); }
        else { CollapseRow(); Hide(); }
    }

    private void Center()
    {
        var screen = _main is null ? Screens.Primary : Screens.ScreenFromWindow(_main) ?? Screens.Primary;
        var area = screen?.WorkingArea;
        if (area is null) return;
        if (_popped)
        {
            var available = Math.Max(80, area.Value.Height / (screen?.Scaling ?? RenderScaling) - 16);
            MaxHeight = available;
            SessionScroll.MaxHeight = Math.Max(30, available - 54);
        }
        var height = (int)Math.Round((double.IsNaN(Height) ? Bounds.Height : Height) * RenderScaling);
        Position = new PixelPoint(area.Value.X, area.Value.Y + Math.Max(0, (area.Value.Height - height) / 2));
    }

    private void OnEnter(object sender, PointerEventArgs e) { if (IsVisible && !_popped) Pop(true); }
    private void OnLeave(object sender, PointerEventArgs e) { if (_popped && !_pinned) { CollapseRow(); if (!_wanted) Hide(); } }
    private void Pin_Click(object sender, RoutedEventArgs e) { _pinned = !_pinned; UpdatePin(); Apply(); }

    private void UpdatePin()
    {
        PinButton.Content = _pinned ? "\uE840" : "\uE718";
        ToolTip.SetTip(PinButton, _pinned ? "Unpin" : "Pin");
        PinButton.Classes.Set("pinned", _pinned);
    }

    private void Pop(bool animate)
    {
        _popped = true;
        Row.IsVisible = true;
        Sliver.IsVisible = false;
        Transitions = animate ? new Transitions { new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(160) } } : null;
        SizeToContent = SizeToContent.Height;
        Width = 94;
        Center();
    }

    private void CollapseRow()
    {
        _popped = false;
        Transitions = null;
        SizeToContent = SizeToContent.Manual;
        Width = ParkedWidth;
        Height = ParkedHeight;
        Row.IsVisible = false;
        Sliver.IsVisible = true;
        if (IsVisible) Center();
    }
}

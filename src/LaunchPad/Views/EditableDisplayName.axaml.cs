namespace LaunchPad.Views;

public sealed class DisplayNameSavedEventArgs(string name, bool reset = false) : EventArgs
{
    public string Name { get; } = name;
    public bool Reset { get; } = reset;
}

public partial class EditableDisplayName : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<EditableDisplayName, string>(nameof(Text), "");
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public event EventHandler<DisplayNameSavedEventArgs>? NameSaved;
    public bool IsEditing => Editor.IsVisible;

    public EditableDisplayName()
    {
        InitializeComponent();
        PropertyChanged += (_, change) => { if (change.Property == TextProperty) Refresh(); };
        Refresh();
    }

    private void Refresh() { CaptionText.Text = Text; ToolTip.SetTip(CaptionButton, Text); }
    public void BeginEdit()
    {
        CaptionButton.IsVisible = false; Editor.IsVisible = true; ResetButton.IsVisible = true; ErrorText.IsVisible = false;
        Editor.Text = Text; Editor.Focus(); Editor.SelectAll();
    }
    public void CancelEdit() { Editor.IsVisible = false; ResetButton.IsVisible = false; ErrorText.IsVisible = false; CaptionButton.IsVisible = true; CaptionButton.Focus(); }
    public void SaveEdit(bool reset = false)
    {
        try { NameSaved?.Invoke(this, new(Editor.Text ?? "", reset)); CancelEdit(); }
        catch (Exception error) { ErrorText.Text = error.Message; ErrorText.IsVisible = true; }
    }
    private void Caption_Click(object? sender, RoutedEventArgs e) { e.Handled = true; BeginEdit(); }
    private void Reset_Click(object? sender, RoutedEventArgs e) { e.Handled = true; SaveEdit(reset: true); }
    private void Name_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsEditing) { CancelEdit(); e.Handled = true; }
        else if (e.Key == Key.Enter && IsEditing) { SaveEdit(); e.Handled = true; }
        else if (e.Key == Key.F2) { BeginEdit(); e.Handled = true; }
    }
}

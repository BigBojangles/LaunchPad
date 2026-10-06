
namespace LaunchPad.Views;

public partial class BackupPrompt : Window
{
    public string? Remote { get; private set; }

    public BackupPrompt(string? origin, bool settings)
    {
        InitializeComponent();
        if (settings)
        {
            BodyText.Text = "Change the remote used before fenced files come back into this project.";
            NotNowButton.Content = "Cancel";
            AcceptButton.Content = "Save";
            UrlBox.Text = origin ?? "";
            OriginText.IsVisible = false;
            return;
        }

        BodyText.Text = "A backup is recommended before the fenced files come back. The project will not change until that backup is done.";
        if (!string.IsNullOrWhiteSpace(origin))
        {
            OriginText.Text = origin;
            UrlLabel.IsVisible = false;
            UrlBox.IsVisible = false;
            AcceptButton.Content = "Use this remote";
            return;
        }

        OriginText.IsVisible = false;
        AcceptButton.Content = "Set up backup";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (UrlBox.IsVisible)
        {
            var url = UrlBox.Text?.Trim() ?? "";
            if (url.Length == 0)
                return;
            Remote = url;
        }
        else
        {
            Remote = OriginText.Text?.Trim() ?? "";
        }

        Close(true);
    }

    private void NotNow_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }
}

using LaunchPad.Services;

namespace LaunchPad.Views;

public partial class ShareDialog : Window
{
    public const string ShareMessage =
        "LaunchPad opens coding-agent projects with Grok Build, Codex CLI, Claude Code, or a custom program. Choose a fenced Linux VM or native Windows execution. Windows beta work is in progress.\n" +
        ExternalLinks.Repo;

    public ShareDialog()
    {
        InitializeComponent();
        MessageText.Text = ShareMessage;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        await UiDialogs.CopyAsync(this, ShareMessage);
        CopyButton.Content = "Copied";
    }

    private async void X_Click(object sender, RoutedEventArgs e)
    {
        await UiDialogs.CopyAsync(this, ShareMessage);
        ExternalLinks.Open("https://x.com/intent/post?text=" + Uri.EscapeDataString(ShareMessage));
    }

    private async void LinkedIn_Click(object sender, RoutedEventArgs e)
    {
        await UiDialogs.CopyAsync(this, ShareMessage);
        ExternalLinks.Open("https://www.linkedin.com/sharing/share-offsite/?url=" +
                           Uri.EscapeDataString(ExternalLinks.Repo));
    }

    private async void TikTok_Click(object sender, RoutedEventArgs e)
    {
        await UiDialogs.CopyAsync(this, ShareMessage);
        ExternalLinks.Open("https://www.tiktok.com/");
    }
}

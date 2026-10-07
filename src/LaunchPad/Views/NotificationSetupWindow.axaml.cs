using LaunchPad.Models;
using LaunchPad.Services;

namespace LaunchPad.Views;

public partial class NotificationSetupWindow : Window
{
    private readonly NotificationService _notifications;
    private bool _loading = true;
    public string? ResultReference { get; private set; }
    public NotificationSetupWindow(NotificationService notifications, string? existingReference = null)
    {
        _notifications = notifications;
        InitializeComponent();
        ProviderBox.ItemsSource = new[] { "Email (SMTP)", "Telegram bot", "Discord webhook", "ntfy" };
        SecurityBox.ItemsSource = new[] { "STARTTLS (usually port 587)", "TLS on connect (usually port 465)" };
        ProviderBox.SelectedIndex = 0;
        SecurityBox.SelectedIndex = 0;
        if (existingReference is not null)
        {
            try
            {
                var value = notifications.Destinations.Read(existingReference);
                ProviderBox.SelectedIndex = (int)value.Provider;
                HostBox.Text = value.SmtpHost; PortBox.Text = value.SmtpPort.ToString();
                SecurityBox.SelectedIndex = (int)value.SmtpSecurity;
                UserBox.Text = value.Username; PasswordBox.Text = value.Password; FromBox.Text = value.From; ToBox.Text = value.To;
                TokenBox.Text = value.Token; ChatBox.Text = value.ChatId; WebhookBox.Text = value.WebhookUrl;
                ServerBox.Text = value.ServerUrl; TopicBox.Text = value.Topic;
            }
            catch { StatusText.Text = "Saved channel could not be opened. Enter a replacement; the old setup was preserved."; }
        }
        _loading = false;
        UpdatePanels();
        Closed += (_, _) => { PasswordBox.Text = TokenBox.Text = WebhookBox.Text = ""; };
    }

    private void Provider_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) PasswordBox.Text = TokenBox.Text = WebhookBox.Text = "";
        UpdatePanels();
    }
    private void UpdatePanels()
    {
        var index = ProviderBox.SelectedIndex;
        EmailPanel.IsVisible = index == 0; TelegramPanel.IsVisible = index == 1;
        DiscordPanel.IsVisible = index == 2; NtfyPanel.IsVisible = index == 3;
        TokenPanel.IsVisible = index is 1 or 3;
        TokenLabel.Text = index == 1 ? "Telegram bot token" : "ntfy access token (optional)";
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ProviderBox.SelectedIndex < 0) throw new ArgumentException("Choose a notification provider.");
            var value = new NotificationDestination { Provider = (NotificationProvider)ProviderBox.SelectedIndex };
            if (value.Provider == NotificationProvider.Email)
            {
                if (!int.TryParse(PortBox.Text, out var port)) throw new ArgumentException("Enter an SMTP port number.");
                value.SmtpHost = HostBox.Text?.Trim(); value.SmtpPort = port; value.SmtpSecurity = (NotificationSmtpSecurity)SecurityBox.SelectedIndex;
                value.Username = UserBox.Text?.Trim(); value.Password = PasswordBox.Text; value.From = FromBox.Text?.Trim(); value.To = ToBox.Text?.Trim();
            }
            else if (value.Provider == NotificationProvider.Telegram) { value.Token = TokenBox.Text?.Trim(); value.ChatId = ChatBox.Text?.Trim(); }
            else if (value.Provider == NotificationProvider.Discord) value.WebhookUrl = WebhookBox.Text?.Trim();
            else { value.ServerUrl = ServerBox.Text?.Trim(); value.Topic = TopicBox.Text?.Trim(); value.Token = TokenBox.Text?.Trim(); }
            ResultReference = _notifications.Destinations.Add(value);
            Close(true);
        }
        catch (ArgumentException error) { StatusText.Text = error.Message; }
        catch { StatusText.Text = "Channel could not be saved. Existing setup was preserved."; }
    }
}

using LaunchPad.Models;
using LaunchPad.Services;

namespace LaunchPad.Views;

public partial class NotificationSetupWindow : Window
{
    private readonly NotificationService _notifications;
    private bool _loading = true;
    private EmailProviderPreset? _previousPreset;
    private EmailProviderPreset? _displayedPreset;
    private string? _previousAddress;
    private string? _previousSenderText;
    private string? _previousSmtpHost;
    private string? _previousSmtpUser;
    public string? ResultReference { get; private set; }
    public NotificationSetupWindow(NotificationService notifications, string? existingReference = null)
    {
        _notifications = notifications;
        InitializeComponent();
        ProviderBox.ItemsSource = new[] { "Email (SMTP)", "Telegram bot", "Discord webhook", "ntfy" };
        SecurityBox.ItemsSource = new[] { "STARTTLS (usually port 587)", "TLS on connect (usually port 465)" };
        EmailServiceBox.ItemsSource = new[] { "Detect from email address" }
            .Concat(EmailProviderPresets.All.Select(preset => preset.Name + (preset.RequiresOAuth ? " (sign-in unavailable)" : "")))
            .Append("Other / custom SMTP").ToArray();
        EmailServiceBox.SelectedIndex = 0;
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
                UserBox.Text = value.Username; PasswordBox.Text = value.Password; FromBox.Text = value.From;
                TokenBox.Text = value.Token; ChatBox.Text = value.ChatId; WebhookBox.Text = value.WebhookUrl;
                ServerBox.Text = value.ServerUrl; TopicBox.Text = value.Topic;
                if (value.Provider == NotificationProvider.Email)
                {
                    var presetIndex = EmailProviderPresets.All.ToList().FindIndex(preset =>
                        string.Equals(preset.Host, value.SmtpHost, StringComparison.OrdinalIgnoreCase)
                        && preset.Port == value.SmtpPort && preset.Security == value.SmtpSecurity);
                    EmailServiceBox.SelectedIndex = presetIndex >= 0
                        ? ReferenceEquals(EmailProviderPresets.All[presetIndex], EmailProviderPresets.Match(value.From)) ? 0 : presetIndex + 1
                        : EmailProviderPresets.All.Count + 1;
                }
            }
            catch { StatusText.Text = "Saved channel could not be opened. Enter a replacement; the old setup was preserved."; }
        }
        // Avalonia queues TextChanged; initialization events may arrive after
        // the constructor. Compare values so loading cannot clear saved login.
        _previousSmtpHost = HostBox.Text;
        _previousSmtpUser = UserBox.Text;
        _loading = false;
        UpdatePanels();
        UpdateEmailPreset(autofill: false);
        Closed += (_, _) => { PasswordBox.Text = TokenBox.Text = WebhookBox.Text = ""; };
    }

    private void Provider_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) PasswordBox.Text = TokenBox.Text = WebhookBox.Text = "";
        UpdatePanels();
        if (!_loading) UpdateEmailPreset(autofill: false);
    }
    private EmailProviderPreset? SelectedEmailPreset() => EmailServiceBox.SelectedIndex == 0
        ? EmailProviderPresets.Match(FromBox.Text)
        : EmailServiceBox.SelectedIndex is > 0 && EmailServiceBox.SelectedIndex <= EmailProviderPresets.All.Count
            ? EmailProviderPresets.All[EmailServiceBox.SelectedIndex - 1] : null;

    private void SenderEmail_Changed(object? sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdateEmailPreset(autofill: true);
    }
    private void EmailService_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateEmailPreset(autofill: true, force: true);
    }
    private void UpdateEmailPreset(bool autofill, bool force = false)
    {
        var senderText = FromBox.Text?.Trim() ?? "";
        var address = EmailProviderPresets.Address(senderText);
        var preset = SelectedEmailPreset();
        if (autofill && (senderText != _previousSenderText || force)) PasswordBox.Text = "";
        if (autofill && (address is not null || force))
        {
            if (address is not null)
            {
                if (force || string.IsNullOrWhiteSpace(UserBox.Text) || UserBox.Text == _previousAddress) UserBox.Text = address;
            }
            if (preset is not null)
            {
                // Automatic detection preserves manual edits. Explicit service
                // selection is the user's request to apply that service's defaults.
                if (force || string.IsNullOrWhiteSpace(HostBox.Text) || HostBox.Text == _previousPreset?.Host) HostBox.Text = preset.Host;
                if (force || string.IsNullOrWhiteSpace(PortBox.Text) || PortBox.Text == (_previousPreset?.Port ?? 587).ToString()) PortBox.Text = preset.Port.ToString();
                if (force || SecurityBox.SelectedIndex < 0 || SecurityBox.SelectedIndex == (int)(_previousPreset?.Security ?? NotificationSmtpSecurity.StartTls))
                    SecurityBox.SelectedIndex = (int)preset.Security;
            }
            else if (!force && _previousPreset is not null)
            {
                // Don't leave an unrelated provider's generated server behind.
                if (HostBox.Text == _previousPreset.Host) HostBox.Text = "";
                if (PortBox.Text == _previousPreset.Port.ToString()) PortBox.Text = "587";
                if (SecurityBox.SelectedIndex == (int)_previousPreset.Security) SecurityBox.SelectedIndex = 0;
            }
            _previousPreset = preset;
            _previousAddress = address;
        }
        else if (!autofill) { _previousPreset = preset; _previousAddress = address; }
        _previousSenderText = senderText;
        _displayedPreset = preset;
        EmailPresetHint.Text = preset is null
            ? "Enter your sending address to detect its service. For a custom domain, choose its email service or enter server settings."
            : preset.Name + ": " + preset.SignInHint;
        EmailSignInHelpButton.IsVisible = preset is not null;
        PasswordBox.IsEnabled = preset?.RequiresOAuth != true;
        SaveChannelButton.IsEnabled = ProviderBox.SelectedIndex != 0 || preset?.RequiresOAuth != true;
        if (preset is null || preset.RequiresOAuth) AdvancedEmailSettings.IsExpanded = true;
        else if (force || !autofill || HostBox.Text == preset.Host && PortBox.Text == preset.Port.ToString()
            && SecurityBox.SelectedIndex == (int)preset.Security) AdvancedEmailSettings.IsExpanded = false;
    }
    private void ServerIdentity_Changed(object? sender, TextChangedEventArgs e)
    {
        var host = HostBox.Text;
        var user = UserBox.Text;
        if (!_loading && (host != _previousSmtpHost || user != _previousSmtpUser)) PasswordBox.Text = "";
        _previousSmtpHost = host;
        _previousSmtpUser = user;
    }
    private void EmailSignInHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (_displayedPreset is null) return;
        try { ExternalLinks.Open(_displayedPreset.HelpUrl); }
        catch { StatusText.Text = "The browser could not open. Visit your email provider's official account security page for sign-in help."; }
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
                if (SelectedEmailPreset()?.RequiresOAuth == true)
                    throw new ArgumentException("Microsoft sign-in is not available yet. Choose another sending account or your separate custom SMTP service.");
                if (!int.TryParse(PortBox.Text, out var port)) throw new ArgumentException("Enter an SMTP port number.");
                value.SmtpHost = HostBox.Text?.Trim(); value.SmtpPort = port; value.SmtpSecurity = (NotificationSmtpSecurity)SecurityBox.SelectedIndex;
                value.Username = UserBox.Text?.Trim(); value.Password = PasswordBox.Text;
                value.From = EmailProviderPresets.Address(FromBox.Text) ?? throw new ArgumentException("Enter your email address.");
                value.To = value.From;
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

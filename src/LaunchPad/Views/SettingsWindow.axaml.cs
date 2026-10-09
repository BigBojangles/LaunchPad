using LaunchPad.Services;
using LaunchPad.Services.Fence;
using LaunchPad.Models;

namespace LaunchPad.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly AgentOption[] _agents;
    private bool _resetTips;
    private readonly bool _nativeOnly;
    private readonly NotificationService _notifications;
    private string? _notificationReference;
    private readonly IWindowsSetupRepair _repair;
    private readonly CancellationTokenSource _testCancellation = new();
    private bool _testBusy;
    private string? _testedReference;
    public SettingsWindow(SettingsStore settings, IHostResources? resources = null, bool nativeOnly = false, NotificationService? notifications = null,
        IWindowsSetupRepair? repair = null)
    {
        _settings = settings;
        _nativeOnly = nativeOnly;
        _repair = repair ?? new WindowsSetupRepair(settings.Paths, nativeOnly);
        _notifications = notifications ?? new NotificationService(settings.Paths);
        _notificationReference = settings.Current.NotificationDestination;
        InitializeComponent();
        ThemeBox.ItemsSource = new[] { "Match Windows", "Light", "Dark" };
        ThemeBox.SelectedIndex = settings.Current.Theme switch { Appearance.Light => 1, Appearance.Dark => 2, _ => 0 };
        SetupRepairButton.Content = nativeOnly ? "Windows test setup…" : "Repair setup…";
        SetupRepairButton.IsEnabled = OperatingSystem.IsWindows() || repair is not null;
        var host = resources ?? HostResources.Current;
        _agents = AgentChoice.Options.Where(agent => agent.Id != AgentChoice.Custom && agent.Enabled).ToArray();
        DefaultAgentBox.ItemsSource = _agents.Select(agent => agent.Label).ToArray();
        DefaultAgentBox.SelectedIndex = Math.Max(0, Array.FindIndex(_agents, agent => agent.Id == settings.Current.DefaultAgent));
        TipsSwitch.IsChecked = settings.Current.ShowTips;
        RememberSignInSwitch.IsChecked = settings.Current.RememberGrokSignIn;
        RememberSignInSwitch.IsEnabled = !nativeOnly;
        NotificationsSwitch.IsChecked = settings.Current.NotificationsEnabled;
        NotificationsHint.Text = _notificationReference is null ? "Off until you configure and enable your own channel." : "A channel is saved. Saving channel setup does not verify delivery.";
        UpdateTestPageTarget();
        Closed += (_, _) => _testCancellation.Cancel();
        var installed = host.InstalledMemoryMegabytes ?? GuestMemory.DefaultMegabytes + 2048;
        var maxGb = Math.Max(2, (installed - 2048) / 1024);
        MemoryBox.ItemsSource = Enumerable.Range(2, maxGb - 1).Select(gb => gb + " GB").ToArray();
        MemoryBox.SelectedIndex = Math.Clamp(GuestMemory.ChooseMegabytes(settings.Current.MachineMemoryMb, installed) / 1024, 2, maxGb) - 2;
        var logical = Math.Max(1, host.LogicalProcessors);
        CoresBox.ItemsSource = Enumerable.Range(1, logical).Select(count => count == 1 ? "1 core" : count + " cores").ToArray();
        CoresBox.SelectedIndex = GuestMemory.ChooseCores(settings.Current.MachineCores, logical) - 1;
        if (nativeOnly)
        {
            MemoryBox.IsEnabled = CoresBox.IsEnabled = false;
            DefaultsHint.Text = "VM defaults are not used by this native-only package. Saved VM allocations are preserved. Project agent choices stay in the project menu.";
        }
    }
    private void ResetTips_Click(object? sender, RoutedEventArgs e) { _resetTips = true; StatusText.Text = "Tips will appear again after saving."; }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Licenses_Click(object? sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(Path.GetDirectoryName(_settings.Paths.ExePath)!, "licenses");
        if (!Directory.Exists(folder))
        {
            StatusText.Text = "The licenses folder is missing from this copy of LaunchPad.";
            return;
        }
        try { ProjectFolders.OpenInExplorer(folder); }
        catch { StatusText.Text = "Could not open the licenses folder: " + folder; }
    }
    private async void RepairSetup_Click(object? sender, RoutedEventArgs e)
        => await new SetupRepairWindow(_repair, _nativeOnly).ShowDialog(this);
    private async void ConfigureNotifications_Click(object? sender, RoutedEventArgs e)
    {
        var setup = new NotificationSetupWindow(_notifications, _notificationReference);
        if (await setup.ShowDialog<bool>(this))
        {
            _notificationReference = setup.ResultReference;
            NotificationsHint.Text = "Channel prepared. Save Settings to apply it; turn on alerts separately for each project.";
            UpdateTestPageTarget();
        }
    }
    private void UpdateTestPageTarget()
    {
        SendTestPageButton.IsEnabled = false;
        TestPageTarget.Text = "Configure a channel to test delivery.";
        if (_notificationReference is null) return;
        try
        {
            TestPageTarget.Text = _notifications.TestTargetSummary(_notificationReference);
            SendTestPageButton.IsEnabled = !_testBusy && _testedReference != _notificationReference;
        }
        catch { TestPageTarget.Text = "Saved channel could not be opened. Configure a replacement; the existing setup is preserved."; }
    }
    private void DisconnectNotifications_Click(object? sender, RoutedEventArgs e)
    {
        if (_testBusy) return;
        try
        {
            _notifications.Disconnect(_settings);
            ClearNotificationSetup();
            StatusText.Text = "Disconnected. Locally saved notification secrets were removed. Revoke the app password or token at your provider separately.";
        }
        catch (NotificationDisconnectException)
        {
            ClearNotificationSetup();
            StatusText.Text = "Alerts are off, but some saved notification secrets could not be removed. Close other settings windows and try Disconnect again.";
        }
        catch { StatusText.Text = "Disconnect could not save the change. No notification secrets were removed. Reopen Settings and try again."; }
    }
    private void ClearNotificationSetup()
    {
        _notificationReference = null;
        _testedReference = null;
        NotificationsSwitch.IsChecked = false;
        NotificationsHint.Text = "Off. Configure a new channel to reconnect.";
        UpdateTestPageTarget();
    }
    private async void SendTestPage_Click(object? sender, RoutedEventArgs e)
    {
        if (_testBusy || _notificationReference is null || _testedReference == _notificationReference) return;
        var reference = _notificationReference;
        _testBusy = true;
        _testedReference = reference;
        SendTestPageButton.IsEnabled = ConfigureNotificationsButton.IsEnabled = DisconnectNotificationsButton.IsEnabled = false;
        StatusText.Text = "Sending one test page…";
        try
        {
            var result = await _notifications.SendTestPageAsync(reference, _testCancellation.Token);
            StatusText.Text = result switch
            {
                ProviderAcceptance.Accepted => "Provider accepted the test page. Check your inbox, spam folder and phone notifications.",
                ProviderAcceptance.NotAccepted => "Provider did not accept the test page. Check the channel settings.",
                _ => "Delivery is unconfirmed. Check your inbox before sending another test; it will not retry automatically."
            };
        }
        catch { StatusText.Text = "The test could not finish or record its result. Check your inbox before trying again; it will not retry automatically."; }
        finally { _testBusy = false; ConfigureNotificationsButton.IsEnabled = DisconnectNotificationsButton.IsEnabled = true; UpdateTestPageTarget(); }
    }
    private async void NotificationHistory_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var items = _notifications.Outbox.Read().TakeLast(20).Reverse().ToArray();
            var text = items.Length == 0 ? "No alerts recorded. Supported agent events and project opt-in are required." : string.Join("\n\n",
                items.Select(item => $"{_settings.DisplayNameFor(item.ProjectPath)} · {item.RunId}\n{item.OccurredUtc:u} · {item.State}\n{item.Status ?? "Waiting for dispatch."}"));
            if (File.Exists(Path.Combine(_notifications.Paths.AppDataDir, "notifications", "delivery-warning.json")))
                text += "\n\nBackground delivery reported a local problem. Saved history is preserved; unconfirmed sends are not retried.";
            var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            close.Classes.Add("GhostButton");
            var body = new DockPanel { Margin = new Thickness(24) };
            DockPanel.SetDock(close, Dock.Bottom);
            body.Children.Add(close);
            body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } });
            var history = new Window { Title = "Notification history", Width = 520, Height = 560, MinWidth = 360, MinHeight = 320,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body };
            close.Click += (_, _) => history.Close();
            await history.ShowDialog(this);
        }
        catch { StatusText.Text = "Alert history could not be read. Saved history was preserved."; }
    }
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DefaultAgentBox.SelectedIndex < 0 || !_nativeOnly && (MemoryBox.SelectedIndex < 0 || CoresBox.SelectedIndex < 0)) throw new ArgumentException("Choose the default agent, memory and CPU count.");
            if (NotificationsSwitch.IsChecked == true)
            {
                if (_notificationReference is null) throw new ArgumentException("Configure a notification channel before enabling alerts.");
                _notifications.Destinations.Read(_notificationReference);
            }
            _settings.SavePreferences(TipsSwitch.IsChecked == true, _agents[DefaultAgentBox.SelectedIndex].Id,
                _nativeOnly ? _settings.Current.MachineMemoryMb : (MemoryBox.SelectedIndex + 2) * 1024,
                _nativeOnly ? _settings.Current.MachineCores : CoresBox.SelectedIndex + 1,
                resetTips: _resetTips, preserveVmDefaults: _nativeOnly,
                notifications: new NotificationPreference(NotificationsSwitch.IsChecked == true, _notificationReference),
                rememberGrokSignIn: _nativeOnly ? null : RememberSignInSwitch.IsChecked == true,
                theme: ThemeBox.SelectedIndex switch { 1 => Appearance.Light, 2 => Appearance.Dark, _ => Appearance.System });
            Appearance.Apply(_settings.Current.Theme);
            try { if (_settings.Current.NotificationsEnabled) _notifications.StartDelivery(); }
            catch { NotificationsHint.Text = "Settings saved; background delivery could not start. It will be retried when LaunchPad opens or an alert is queued."; return; }
            Close(true);
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
}

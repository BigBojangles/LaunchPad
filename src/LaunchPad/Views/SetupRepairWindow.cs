using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public sealed class SetupRepairWindow : Window
{
    private readonly IWindowsSetupRepair _repair;
    private readonly bool _nativeOnly;
    private readonly StackPanel _checks = new() { Name = "SetupChecks", Spacing = 10 };
    private readonly TextBlock _status = new() { Name = "RepairStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _check = new() { Name = "CheckSetupButton", Content = "Check again" };
    private readonly Button _run = new() { Name = "RepairSetupButton", Content = "Repair setup" };
    private readonly Button _close = new() { Name = "CloseRepairButton", Content = "Close" };
    private bool _running;

    public SetupRepairWindow(IWindowsSetupRepair repair, bool nativeOnly)
    {
        _repair = repair;
        _nativeOnly = nativeOnly;
        Title = nativeOnly ? "Windows test setup" : "Repair setup";
        Width = 540; Height = 520; MinWidth = 380; MinHeight = 380;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var title = new TextBlock { Text = Title }; title.Classes.Add("TitleText");
        var explanation = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = nativeOnly
                ? "Optional setup for restricted Windows testing. Native agents keep using your Windows account. This action does not install or enable a VM."
                : "Check the Windows test account, installed VM files and Windows virtualization. Repair restores required runtime access and can request administrator approval for account or Windows feature setup." };
        explanation.Classes.Add("HintText");
        var preservation = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Your projects, settings, credentials and saved VM disks are preserved. Repair never replaces images or resets an existing account password. Missing files or credentials are reported for recovery." };
        preservation.Classes.Add("HintText");
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(title); content.Children.Add(explanation); content.Children.Add(preservation); content.Children.Add(_checks);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        foreach (var button in new[] { _check, _run, _close }) { button.Classes.Add("GhostButton"); buttons.Children.Add(button); }
        _run.Classes.Add("PrimaryButton");
        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(_status); footer.Children.Add(buttons);
        var body = new DockPanel { Margin = new Thickness(24) }; DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer); body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = content });
        Content = body;
        var scope = new NameScope(); NameScope.SetNameScope(this, scope);
        foreach (var control in new Control[] { _checks, _status, _check, _run, _close }) scope.Register(control.Name!, control);
        _check.Click += async (_, _) => await Run(false);
        _run.Click += async (_, _) => await Run(true);
        _close.Click += (_, _) => Close();
        Opened += async (_, _) => await Run(false);
        Closing += (_, args) => { if (_running) args.Cancel = true; };
    }

    private async Task Run(bool repair)
    {
        if (_running) return;
        _running = true; _check.IsEnabled = _run.IsEnabled = _close.IsEnabled = false;
        _status.Text = repair ? "Repairing setup. Complete any Windows administrator prompt; the app will stay open until setup finishes." : "Checking setup…";
        try
        {
            var report = await (repair ? _repair.RepairAsync() : _repair.CheckAsync());
            _checks.Children.Clear();
            foreach (var check in report.Checks)
            {
                var name = new TextBlock { Text = (check.Ready ? "Ready · " : "Needs attention · ") + check.Name };
                name.Classes.Add("BodyText");
                var detail = new TextBlock { Text = check.Detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap }; detail.Classes.Add("HintText");
                _checks.Children.Add(name); _checks.Children.Add(detail);
            }
            _status.Text = report.RestartRequired ? "Windows requires a restart. Restart when convenient, then reopen LaunchPad."
                : report.Ready ? (_nativeOnly
                    ? "Setup checks passed. A restricted Windows app test is still needed to verify the complete workflow."
                    : "Setup checks passed. A real VM launch or restricted app test is still needed to verify the complete workflow.")
                : "Setup needs attention. Follow the details above; existing state was preserved.";
        }
        catch { _status.Text = "Setup could not be checked or repaired. Existing state was preserved."; }
        finally { _running = false; _check.IsEnabled = _run.IsEnabled = _close.IsEnabled = true; }
    }
}

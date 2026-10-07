using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class ProjectPermissionsWindow : Window
{
    private static readonly string[] Presets = ["strict", "standard", "troubleshoot"];
    private readonly AppServices _services;
    private readonly string _project;
    private readonly bool _native;
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromSeconds(1) };
    private SessionOwnerIdentity? _owner;

    public ProjectPermissionsWindow(AppServices services, string project)
    {
        _services = services; _project = Path.GetFullPath(project);
        _native = services.Runtime.NativeOnly || services.Settings.LaunchModeFor(_project) == "native";
        InitializeComponent();
        ProjectLabel.Text = services.Settings.DisplayNameFor(_project);
        ToolTip.SetTip(ProjectLabel, ProjectLabel.Text);
        ToolBox.ItemsSource = new[] { "dotnet", "node", "python", "powershell", "project" };
        ToolBox.SelectedIndex = 0; ProgramBox.IsEnabled = false;
        ToolBox.SelectionChanged += (_, _) => ProgramBox.IsEnabled = ToolBox.SelectedItem as string == "project";
        try
        {
            var policy = services.Settings.PermissionPolicyFor(_project);
            PresetSlider.Value = Array.IndexOf(Presets, policy.Preset);
            DestinationsBox.Text = string.Join("\n", policy.Destinations.Select(item => item.Host + ":" + item.Port));
        }
        catch (Exception error) { SaveButton.IsEnabled = false; StatusText.Text = error.Message; }
        PresetSlider.IsEnabled = DestinationsBox.IsEnabled = !_native;
        SaveButton.IsEnabled &= !_native;
        PresetSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) ShowRequested(); };
        _monitor.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _monitor.Start(); };
        Closed += (_, _) => _monitor.Stop();
        ShowRequested(); Refresh();
    }

    private string SelectedPreset => Presets[Math.Clamp((int)Math.Round(PresetSlider.Value), 0, 2)];
    private void ShowRequested() => RequestedText.Text = _native ? "Native mode has direct Windows access. VM presets do not apply."
        : SelectedPreset == "strict" ? "Strict requests destination restrictions and confirmation before Windows tests. Network enforcement is not available yet; this choice prevents a new VM launch."
        : SelectedPreset == "troubleshoot" ? "Troubleshoot uses the Standard fence with individually granted, expiring test permissions."
        : "Standard uses the current fenced setup and your existing Windows test confirmation choice.";

    private void Refresh()
    {
        try
        {
            var settings = new SettingsStore(_services.Paths);
            var saved = settings.PermissionPolicyFor(_project);
            _owner = _native ? null : PermissionPolicies.LiveOwner(_project);
            var applied = _owner is null ? null : PermissionPolicies.Applied(_services.Paths, _project, _owner);
            EffectiveText.Text = _native ? "Current mode: Native (no sandbox)."
                : _owner is null ? "Saved: " + saved.Preset + ". No active VM."
                : applied is null ? "Saved: " + saved.Preset + ". The running VM has no applied-policy record; protection is unverified."
                : "Saved: " + saved.Preset + ". Running VM: " + applied.Policy.Preset + " with Standard network access."
                    + (saved.Preset != applied.Policy.Preset ? " Reopen the VM to apply the new preset." : "");
            var exceptions = _owner is null ? null : PermissionPolicies.Exceptions(_services.Paths, _project, _owner);
            var stamp = DateTimeOffset.UtcNow;
            var uptime = Environment.TickCount64;
            ExceptionsText.Text = exceptions is null || exceptions.Grants.Length == 0 ? "No temporary test permissions."
                : string.Join("\n", exceptions.Grants.Select(item => $"{item.Tool}{(item.Program is null ? "" : ": " + item.Program)} — "
                    + (exceptions.EndedUtc is not null || item.ExpiresUtc <= stamp || item.ExpiresUptimeMs <= uptime || applied?.TemporaryPermissionsDisabledReason is not null ? "expired" : "expires " + item.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss"))));
            if (applied?.TemporaryPermissionsDisabledReason is not null) EffectiveText.Text += " " + applied.TemporaryPermissionsDisabledReason;
            GrantButton.IsEnabled = !_native && _owner is not null && applied is { EndedUtc: null, TemporaryPermissionsDisabledReason: null } && applied.Policy.Preset == "troubleshoot" && saved.Preset == "troubleshoot";
            RevokeButton.IsEnabled = !_native && _owner is not null && exceptions is { EndedUtc: null } && exceptions.Grants.Any(item => item.ExpiresUtc > stamp && item.ExpiresUptimeMs > uptime);
        }
        catch (Exception error)
        { GrantButton.IsEnabled = RevokeButton.IsEnabled = false; EffectiveText.Text = "Effective permissions unavailable: " + error.Message; }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_native) throw new InvalidOperationException("VM presets do not apply to Native mode.");
            var destinations = (DestinationsBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line =>
                {
                    var pieces = line.Split(':');
                    if (pieces.Length != 2 || !int.TryParse(pieces[1], out var port)) throw new ArgumentException("Use hostname:port, one destination per line.");
                    return new ApprovedDestination(pieces[0], port);
                }).ToArray();
            _services.Settings.SavePermissionPolicy(_project, new(1, SelectedPreset, destinations));
            StatusText.Text = SelectedPreset == "strict" ? "Strict saved. New VM launches are refused until destination filtering is available; Windows tests now require confirmation."
                : "Preset saved. Reopen this VM to change its applied preset. Existing explicit Windows test confirmation is preserved.";
            Refresh();
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void Grant_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Refresh();
            if (_owner is null) throw new InvalidOperationException("No active project VM.");
            var settings = new SettingsStore(_services.Paths);
            var tool = ToolBox.SelectedItem as string ?? throw new ArgumentException("Choose a test tool.");
            var grant = PermissionPolicies.Grant(_services.Paths, settings, _project, _owner, tool, tool == "project" ? ProgramBox.Text?.Trim() : null);
            StatusText.Text = "Temporary permission expires at " + grant.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss") + " or when this session ends.";
            Refresh();
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private void Revoke_Click(object? sender, RoutedEventArgs e)
    {
        try { Refresh(); if (_owner is null) throw new InvalidOperationException("No active project VM."); PermissionPolicies.Revoke(_services.Paths, _project, _owner); StatusText.Text = "Temporary permissions revoked. The owner checks revocation and stops an unconfirmed affected test."; Refresh(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

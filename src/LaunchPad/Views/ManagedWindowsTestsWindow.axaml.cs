using System.Text;
using LaunchPad.Services;
using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class ManagedWindowsTestsWindow : Window
{
    private readonly AppServices _services;
    private readonly string _project;
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private string? _pendingCommand, _logsFor;
    private DateTimeOffset _pendingUntil;
    private string? _commandNotice;

    public ManagedWindowsTestsWindow(AppServices services, string project)
    {
        _services = services; _project = Path.GetFullPath(project);
        InitializeComponent();
        ProjectLabel.Text = services.Settings.DisplayNameFor(_project);
        ToolTip.SetTip(ProjectLabel, ProjectLabel.Text);
        try { PermissionBox.SelectedIndex = services.Settings.WindowsTestPermissionFor(_project) == "confirm" ? 1 : 0; }
        catch (Exception error) { PermissionBox.SelectedIndex = -1; StatusText.Text = error.Message; }
        _monitor.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _monitor.Start(); };
        Closed += (_, _) => _monitor.Stop(); // The guardian owns the test, not this view.
        UpdateActions();
    }

    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var policy = new SettingsStore(_services.Paths).PermissionPolicyFor(_project);
            PermissionHint.Text = policy.Preset == "strict"
                ? "Strict requires confirmation for every test, even when the base choice below is Automatic. Confirmation lasts one minute and applies only to that request."
                : "The saved base choice below is preserved. Troubleshoot may allow a named tool for this session until its permission expires. Runs still use a copied snapshot and the non-admin Windows test account.";
            var previous = Selected?.State;
            var entries = WindowsTestControls.List(_services.Paths, _project);
            RunsList.ItemsSource = entries;
            RunsList.SelectedItem = entries.FirstOrDefault(entry => entry.State.Generation == previous?.Generation && entry.State.RequestId == previous.RequestId)
                ?? entries.FirstOrDefault();
            EmptyText.IsVisible = entries.Count == 0;
            if (Selected is { } selected)
            {
                var state = selected.State;
                RequestText.Text = $"{state.Tool}{(state.Program is null ? "" : " · " + state.Program)}\n" +
                    string.Join(" ", state.Arguments.Select(argument => System.Text.Json.JsonSerializer.Serialize(argument))) +
                    $"\n{(state.Interactive ? "Interactive app" : "Command test")} · Folder: {state.WorkingDirectory} · Limit: {state.TimeoutSeconds}s" +
                    $"\nRequest {state.RequestId} · Session {state.Generation}";
                if (_pendingCommand is not null && _pendingCommand == state.AcknowledgedCommand)
                { _pendingCommand = null; StatusText.Text = state.Error ?? "Action acknowledged by the test owner."; }
                if (_pendingCommand is not null && (state.Stage == "finished" || !WindowsTestControls.OwnerAlive(state) || DateTimeOffset.UtcNow >= _pendingUntil))
                { _pendingCommand = null; _commandNotice = "The requested action was not acknowledged; review the retained result before retrying."; }
                if (_pendingCommand is null)
                {
                    StatusText.Text = state.Stage == "finished" ? $"{state.Outcome}; exit code: {state.ExitCode?.ToString() ?? "unavailable"}. {state.Error}"
                        : !WindowsTestControls.OwnerAlive(state) ? "The test owner stopped. The retained copy is available; completion is unverified."
                        : state.Stage == "awaiting-approval" ? $"Waiting for your decision until {state.ApprovalDeadline:HH:mm:ss}."
                        : state.Stage + (state.Error is null ? "" : ": " + state.Error);
                    if (_commandNotice is not null) StatusText.Text += " " + _commandNotice;
                }
                if (state.Stage == "finished" && _logsFor != selected.Directory)
                {
                    _logsFor = selected.Directory;
                    LogsText.Text = ReadLogs(selected.Directory);
                }
                else if (state.Stage != "finished") { _logsFor = null; LogsText.Text = "Logs are retained after the test finishes."; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { StatusText.Text = "Windows test history is unavailable: " + error.Message; RunsList.ItemsSource = null; }
        finally { _refreshing = false; UpdateActions(); }
    }

    private WindowsTestControlEntry? Selected => RunsList.SelectedItem as WindowsTestControlEntry;
    private void Runs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (!_refreshing) { _pendingCommand = null; _commandNotice = null; _logsFor = null; Refresh(); } }

    private void UpdateActions()
    {
        var state = Selected?.State;
        var live = state is not null && WindowsTestControls.OwnerAlive(state);
        ApproveButton.IsEnabled = DenyButton.IsEnabled = live && _pendingCommand is null && state!.Stage == "awaiting-approval"
            && state.ApprovalDeadline > DateTimeOffset.UtcNow;
        CancelTestButton.IsEnabled = live && state!.Stage is "preparing" or "awaiting-approval" or "running";
        ShowButton.IsEnabled = ReturnButton.IsEnabled = live && _pendingCommand is null && state!.Stage == "running" && state.DesktopAvailable;
        ResultsButton.IsEnabled = Selected is not null;
    }

    private void Command(string action)
    {
        if (Selected is not { } entry) return;
        try
        {
            _pendingCommand = WindowsTestControls.Send(entry, action);
            _pendingUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            _commandNotice = null;
            StatusText.Text = action == "cancel" ? "Cancellation requested. Waiting for owned-process cleanup and saved results."
                : "Action requested. Waiting for the test owner.";
            UpdateActions();
        }
        catch (Exception error) { StatusText.Text = "Action was not sent: " + error.Message; }
    }
    private void Approve_Click(object? sender, RoutedEventArgs e) => Command("approve");
    private void Deny_Click(object? sender, RoutedEventArgs e) => Command("deny");
    private void CancelTest_Click(object? sender, RoutedEventArgs e) => Command("cancel");
    private void Show_Click(object? sender, RoutedEventArgs e) => Command("show");
    private void Return_Click(object? sender, RoutedEventArgs e) => Command("return");
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void SavePermission_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (PermissionBox.SelectedIndex is not (0 or 1)) throw new ArgumentException("Choose a Windows test permission.");
            _services.Settings.SaveWindowsTestPermission(_project, PermissionBox.SelectedIndex == 1 ? "confirm" : "automatic");
            StatusText.Text = "Windows test permission saved. The owner checks it before the next launch.";
        }
        catch (Exception error) { StatusText.Text = "Permission was not saved: " + error.Message; }
    }
    private void Results_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        try
        {
            if (!FenceFiles.TryResolveUnlinked(entry.Directory, "results", out var path) || !Directory.Exists(path))
                throw new IOException("Saved results are not available yet.");
            _services.Desktop.OpenProjectFolder(path);
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private async void ManualPreview_Click(object? sender, RoutedEventArgs e) =>
        await new WindowsTestWindow(_services, _project).ShowDialog(this);

    private static string ReadLogs(string run)
    {
        var text = new StringBuilder();
        foreach (var name in new[] { "stdout", "stderr" })
        {
            if (!FenceFiles.TryResolveUnlinked(run, "results/logs/" + name + ".txt", out var path) || !File.Exists(path)) continue;
            using var input = WindowsTestProtocol.OpenResultSource(path);
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var chars = new char[32768];
            var count = reader.ReadBlock(chars, 0, chars.Length);
            text.AppendLine(name + ":").Append(chars, 0, count).AppendLine();
            if (!reader.EndOfStream) text.AppendLine("Preview truncated. Open saved results for the complete retained log.");
        }
        return text.Length == 0 ? "No output logs were returned for this test." : text.ToString();
    }
}

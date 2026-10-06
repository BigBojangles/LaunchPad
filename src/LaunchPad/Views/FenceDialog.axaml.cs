using LaunchPad.Services;
using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class FenceDialog : Window
{
    private readonly AppServices _services;
    private readonly string _projectPath;
    private readonly IFencedProjectSession _session;
    private CancellationTokenSource? _start;
    private readonly FenceStartAvailability _availability;
    private bool _copyBusy;
    private bool _sessionRunning;
    private ProjectRecovery? _recovery;

    public string ReportedWarnings { get; private set; } = "";

    public string ReportedStatus { get; private set; } = "";

    public FenceDialog(AppServices services, string projectPath)
    {
        _services = services;
        _projectPath = projectPath;
        _session = services.Runtime.CreateFencedSession();
        _availability = services.Runtime.FenceStartAvailability;
        InitializeComponent();
        Closed += (_, _) => _start?.Cancel();
        RunningText.Text = "Fenced start runs the selected agent on a copy of this project inside its VM. The Windows project folder is not mounted there.";
        ClipboardText.Text = SealText.Clipboard;
        NetworkText.Text = SealText.Network;
        WindowsGrokText.Text = SealText.WindowsGrok;
        ShowAside();
        if (!string.IsNullOrEmpty(_availability.Reason))
            StatusText.Text = _availability.Reason;
    }

    private void ShowAside()
    {
        var selected = (RecoveryBox.SelectedItem as SavedReturn)?.Directory;
        _recovery = _session.ReadRecovery(_projectPath);
        _sessionRunning = _services.Runtime.IsFencedOpen(_projectPath);
        var recoveryMode = _sessionRunning || _recovery.RestartBlocked || _recovery.CanResume || _recovery.Returns.Count > 0;
        Title = recoveryMode ? "Saved work" : "Fenced start";
        HeadingText.Text = Title;
        ClipboardText.IsVisible = !recoveryMode;
        NetworkText.IsVisible = !recoveryMode;
        WindowsGrokText.IsVisible = !recoveryMode;
        RunningText.IsVisible = !recoveryMode;
        RecoveryBox.ItemsSource = _recovery.Returns;
        RecoveryBox.SelectedItem = _recovery.Returns.FirstOrDefault(item => item.Directory == selected)
            ?? _recovery.Returns.FirstOrDefault(item => item.CanRetry) ?? _recovery.Returns.FirstOrDefault();
        RecoveryBox.IsVisible = _recovery.Returns.Count > 0;
        RecoveryText.Text = _sessionRunning
            ? "The project VM is running. Review saved copies now; close the session before retrying copy-back."
            : _recovery.Message;
        RecoveryText.IsVisible = recoveryMode;
        OpenSavedVmButton.IsVisible = _recovery.RestartBlocked;
        ResumeSavedButton.IsVisible = _recovery.CanResume;
        ResumeSavedButton.IsEnabled = !_availability.Blocked && !_copyBusy && _start is null && !_sessionRunning;
        StartButton.IsEnabled = !_availability.Blocked && !_recovery.RestartBlocked && !_copyBusy && _start is null && !_sessionRunning;
        ShowSelectedReturn();
    }

    private void ReturnSelection_Changed(object? sender, SelectionChangedEventArgs e) => ShowSelectedReturn();

    private void ShowSelectedReturn()
    {
        var selected = RecoveryBox.SelectedItem as SavedReturn;
        var waiting = selected is not null;
        AsideText.IsVisible = waiting;
        ExplorerText.IsVisible = waiting;
        StubText.IsVisible = waiting;
        CopyBackText.IsVisible = waiting;
        CopyBackButton.IsVisible = waiting;
        CopyBackButton.IsEnabled = selected?.CanRetry == true && !_copyBusy && !_sessionRunning;
        OpenReturnButton.IsVisible = waiting;
        OpenReturnButton.IsEnabled = selected?.CanReview == true;
        if (!waiting)
            return;

        AsideText.Text = "The received copy remains separate from your Windows project.";
        ExplorerText.Text = "Review files opens the Windows recovery folder. Copy-back checks the scanner before applying changes.";
        CopyBackText.Text = "Retry verifies the received content, checks the file scanner, and stops on conflicts with Windows edits.";
        StubText.Text = selected!.Message;
    }

    private void ShowWarnings()
    {
        ReportedWarnings = _session.Warnings.Count == 0
            ? ""
            : string.Join(Environment.NewLine, _session.Warnings);
        if (!IsLoaded)
            return;

        if (_session.Warnings.Count == 0)
        {
            WarningsText.IsVisible = false;
            return;
        }

        WarningsText.Text = ReportedWarnings;
        WarningsText.IsVisible = true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Runtime.IsFencedOpen(_projectPath))
        {
            StatusText.Text = SealText.AlreadyRunning;
            ReportedStatus = StatusText.Text;
            return;
        }

        _start = new CancellationTokenSource();
        ShowAside();
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (!IsLoaded)
                    return;
                StatusText.Text = text;
                ReportedStatus = text;
            });
            await _session.StartAsync(
                _projectPath,
                Placement(),
                progress,
                _start.Token,
                _ =>
                {
                    try { Dispatcher.UIThread.Post(ShowWarnings); }
                    catch { }
                }, leaveRunning: true);
            ShowAside();
            ShowWarnings();
            if (IsLoaded)
                StatusText.Text = "The project session was started.";
        }
        catch (OperationCanceledException)
        {
            ShowWarnings();
            if (IsLoaded)
                StatusText.Text = FencePanel.Stopped;
        }
        catch (Exception ex)
        {
            _services.Log.Write("Fenced start failed: " + ex.Message);
            if (IsLoaded)
                StatusText.Text = ex.Message;
            ReportedStatus = ex.Message;
        }
        finally
        {
            _start?.Dispose();
            _start = null;
            if (IsLoaded)
            {
                ShowAside();
                ReportedStatus = StatusText.Text ?? "";
            }
        }
    }

    private async void CopyBack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CopyBackButton.IsEnabled = false;
            _copyBusy = true;
            StartButton.IsEnabled = false;
            RecoveryBox.IsEnabled = false;
            var selected = RecoveryBox.SelectedItem as SavedReturn
                ?? throw new InvalidOperationException("Select a saved return first.");
            await _session.CopyBackToProjectAsync(_projectPath, selected.Directory);
            StatusText.Text = "The returned files were verified and applied.";
            ShowAside();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _copyBusy = false;
            RecoveryBox.IsEnabled = true;
            ShowAside();
            ReportedStatus = StatusText.Text ?? "";
        }
    }

    private void OpenReturn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (RecoveryBox.SelectedItem is SavedReturn { CanReview: true } selected)
            {
                if (!FenceFiles.TryResolveUnlinked(selected.ReviewPath, "review/check", out _))
                    throw new IOException("The saved copy contains a link and could not be opened safely.");
                _services.Desktop.OpenProjectFolder(selected.ReviewPath);
            }
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void OpenSavedVm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_recovery is { } recovery && FenceFiles.TryResolveUnlinked(recovery.SessionDirectory, "session.qcow2", out _))
                _services.Desktop.OpenProjectFolder(recovery.SessionDirectory);
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void ResumeSaved_Click(object? sender, RoutedEventArgs e)
    {
        if (_start is not null || _services.Runtime.IsFencedOpen(_projectPath)) return;
        _start = new CancellationTokenSource();
        ShowAside();
        try
        {
            var progress = new Progress<string>(text => { if (IsLoaded) StatusText.Text = text; });
            await _session.ResumeSavedAsync(_projectPath, Placement(), progress, _start.Token);
            StatusText.Text = "The saved VM was opened. No Windows project files were sent.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Opening the saved VM was cancelled. Its disks were preserved."; }
        catch (Exception error) { StatusText.Text = error.Message; _services.Log.Write("Saved VM open failed: " + error.Message); }
        finally { _start.Dispose(); _start = null; ShowAside(); ReportedStatus = StatusText.Text ?? ""; }
    }

    private LaunchPlacement? Placement()
    {
        var owner = Owner as Window;
        if (owner is null)
            return null;

        try
        {
            var scale = owner.RenderScaling;
            var x = owner.Position.X + (int)Math.Round(28 * scale);
            var y = owner.Position.Y + (int)Math.Round(28 * scale);
            var columns = Math.Clamp((int)Math.Round(owner.Bounds.Width / 8.5), 80, 140);
            var rows = Math.Clamp((int)Math.Round(owner.Bounds.Height / 18.0), 24, 48);
            return new LaunchPlacement(x, y, columns, rows);
        }
        catch
        {
            return null;
        }
    }
}

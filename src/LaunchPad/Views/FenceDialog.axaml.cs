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
        Closing += (_, args) => { if (_copyBusy) args.Cancel = true; };
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
        Title = _sessionRunning ? "Project is already open"
            : _recovery.CanResume ? "Continue your project"
            : recoveryMode ? "Saved work needs attention" : "Open project";
        HeadingText.Text = Title;
        ClipboardText.IsVisible = !recoveryMode;
        NetworkText.IsVisible = !recoveryMode;
        WindowsGrokText.IsVisible = !recoveryMode;
        RunningText.IsVisible = !recoveryMode;
        RecoveryBox.ItemsSource = _recovery.Returns;
        RecoveryBox.SelectedItem = _recovery.Returns.FirstOrDefault(item => item.Directory == selected)
            ?? _recovery.Returns.FirstOrDefault();
        RecoveryBox.IsVisible = _recovery.Returns.Count > 0;
        RecoveryText.Text = _sessionRunning
            ? "This project is already open. Close this page to keep working in its agent window."
            : _recovery.CanResume && _recovery.RestartBlocked
                ? "Your saved VM is available. LaunchPad could not finish saving its files back to your Windows folder. Continue project opens the saved VM without sending Windows files into it."
                : _recovery.CanResume
                    ? "Continue project opens your saved VM. Saved file copies are available under More."
                    : "LaunchPad kept the saved files. Use More to review what happened and the available copies.";
        RecoveryText.IsVisible = recoveryMode;
        RecoveryDetailsText.Text = _recovery.Message;
        RecoveryMore.IsVisible = recoveryMode;
        OpenSavedVmButton.IsVisible = _recovery.RestartBlocked;
        ResumeSavedButton.IsVisible = _recovery.CanResume;
        ResumeSavedButton.IsEnabled = !_availability.Blocked && !_copyBusy && _start is null && !_sessionRunning;
        StartButton.IsEnabled = !_availability.Blocked && !_recovery.RestartBlocked && !_copyBusy && _start is null && !_sessionRunning;
        StartButton.IsVisible = !_recovery.CanResume && !_recovery.RestartBlocked && !_sessionRunning;
        DismissButton.IsEnabled = !_copyBusy;
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
        CopyBackButton.IsVisible = selected?.CanRetry == true;
        CopyBackButton.IsEnabled = selected?.CanRetry == true && !_copyBusy && !_sessionRunning && _start is null;
        SelectedReturnText.IsVisible = selected?.CanRetry == true;
        SelectedReturnText.Text = selected?.CanRetry == true
            ? "Save to Windows uses the saved copy from " + selected.SavedUtc.ToLocalTime().ToString("g")
                + ". It checks for Windows edits before replacing files." : "";
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

    private void Dismiss_Click(object? sender, RoutedEventArgs e)
    {
        if (!_copyBusy) Close();
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
            {
                StatusText.Text = "The project session was started.";
                ReportedStatus = StatusText.Text;
                Close();
            }
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
            DismissButton.IsEnabled = false;
            ResumeSavedButton.IsEnabled = false;
            StatusText.Text = "Saving to your Windows folder… Please wait before closing this page.";
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
            DismissButton.IsEnabled = true;
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
        if (_copyBusy || _start is not null || _services.Runtime.IsFencedOpen(_projectPath)) return;
        _start = new CancellationTokenSource();
        ShowAside();
        try
        {
            var progress = new Progress<string>(text => { if (IsLoaded) StatusText.Text = text; });
            await _session.ResumeSavedAsync(_projectPath, Placement(), progress, _start.Token);
            StatusText.Text = "The saved VM was opened. No Windows project files were sent.";
            ReportedStatus = StatusText.Text;
            if (IsLoaded) Close();
        }
        catch (OperationCanceledException) { StatusText.Text = "Opening the saved VM was cancelled. Its disks were preserved."; }
        catch (Exception error) { StatusText.Text = error.Message; _services.Log.Write("Saved VM open failed: " + error.Message); }
        finally
        {
            _start.Dispose(); _start = null;
            if (IsLoaded) { ShowAside(); ReportedStatus = StatusText.Text ?? ""; }
        }
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

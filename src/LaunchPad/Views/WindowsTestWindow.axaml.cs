using System.Diagnostics;
using Avalonia.Platform.Storage;
using LaunchPad.Services;
using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class WindowsTestWindow : Window
{
    private readonly AppServices _services;
    private readonly string _project;
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _copyCancellation;
    private InteractiveTestDesktop? _desktop;
    private Process? _normalDemo;
    private WindowsTestCopy? _copy;
    private string? _program;
    private bool _busy, _closed;

    public WindowsTestWindow(AppServices services, string project)
    {
        _services = services;
        _project = Path.GetFullPath(project);
        InitializeComponent();
        ProjectLabel.Text = services.Settings.DisplayNameFor(project);
        _monitor.Tick += (_, _) => ObserveExit();
        Closed += (_, _) => { _closed = true; _copyCancellation?.Cancel(); if (!_busy) StopOwnedTest(); };
    }

    private async void ChooseApp_Click(object? sender, RoutedEventArgs e)
    {
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Built Windows app inside this project", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Windows program") { Patterns = ["*.exe"] }] });
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        if (FenceFiles.Relative(_project, path) is null)
        { StatusText.Text = "Choose an .exe inside this project. Use the built app's output folder."; return; }
        _program = path;
        ProgramLabel.Text = path;
        UpdateActions();
    }

    private async void NormalDemo_Click(object? sender, RoutedEventArgs e) => await StartAsync(demo: true, testAccount: false);
    private async void AccountDemo_Click(object? sender, RoutedEventArgs e) => await StartAsync(demo: true, testAccount: true);
    private async void RunApp_Click(object? sender, RoutedEventArgs e) => await StartAsync(demo: false, testAccount: true);

    private async Task StartAsync(bool demo, bool testAccount)
    {
        if (_busy || _desktop is not null || _normalDemo is not null) return;
        if (!OperatingSystem.IsWindows()) { StatusText.Text = "This preview is Windows-only."; return; }
        if (testAccount && !TestUserRunner.LaunchAccountReady())
        { StatusText.Text = "The Windows test account is not set up. Open Settings → Windows test setup or Repair setup to prepare it."; return; }
        _busy = true;
        _copy = null;
        CopyPathText.Text = "";
        _copyCancellation = new CancellationTokenSource();
        UpdateActions();
        StatusText.Text = "Preparing a separate working copy…";
        try
        {
            var runs = Path.Combine(_services.Paths.AppDataDir, "windows-tests");
            var cancellation = _copyCancellation.Token;
            var copy = await Task.Run(() => demo ? WindowsTestWorkspace.StageDemo(runs)
                : WindowsTestWorkspace.StageProgram(runs, _project, _program!, cancellation));
            _copy = copy;
            if (_closed || cancellation.IsCancellationRequested) { StatusText.Text = "Copy canceled. Nothing launched; the copy is retained."; return; }
            CopyPathText.Text = copy.Directory;
            if (testAccount)
            {
                // Grants apply only to this newly created working copy.
                RestrictedRuntimeAccess.ModifyDirectory(Path.GetDirectoryName(copy.Executable)!);
                StatusText.Text = "Starting the app as BuildLaunchTest…";
                var desktop = await Task.Run(() => InteractiveTestDesktop.Create(300));
                if (_closed || cancellation.IsCancellationRequested) { desktop.Dispose(); return; }
                _desktop = desktop;
                var process = await Task.Run(() => desktop.StartProcess(copy.Executable, [], Path.GetDirectoryName(copy.Executable)!));
                if (_closed || cancellation.IsCancellationRequested) { StopOwnedTest(); return; }
                WindowsTestWorkspace.SaveResult(copy.Directory, new { stage = "process-started", identity = TestUserRunner.UserName,
                    processId = process.Id, desktop = desktop.Name, original = copy.Source });
                var idle = await Task.Run(() =>
                {
                    try { return process.WaitForInputIdle(2000); }
                    catch (InvalidOperationException) { return false; }
                });
                if (_closed || cancellation.IsCancellationRequested) { StopOwnedTest(); return; }
                if (process.HasExited) { ObserveExit(); return; }
                StatusText.Text = "Process started. Use Show test desktop to check the app. Return leaves it running.";
                if (idle) ShowTestDesktop();
            }
            else
            {
                _normalDemo = Process.Start(new ProcessStartInfo(copy.Executable)
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(copy.Executable)!, WindowStyle = ProcessWindowStyle.Normal })
                    ?? throw new IOException("The normal checklist demo could not start.");
                WindowsTestWorkspace.SaveResult(copy.Directory, new { stage = "normal-demo-started", identity = Environment.UserName, processId = _normalDemo.Id });
                StatusText.Text = "The normal checklist demo process started using your Windows account. It is outside the sandbox.";
            }
            _monitor.Start();
        }
        catch (Exception error)
        {
            var nativeCode = (error as System.ComponentModel.Win32Exception)?.NativeErrorCode;
            var detail = nativeCode is int code
                ? $"Windows error {code}: {new System.ComponentModel.Win32Exception(code).Message} " + error.Message
                : error.Message;
            if (nativeCode == 267) detail += " The test account could not use the launcher or working-copy folder; preview setup needs repair.";
            StatusText.Text = "Windows test failed: " + detail;
            if (_copy is not null)
                TrySave(new { stage = "start-failed", error = detail, nativeCode, original = _copy.Source });
            StopOwnedTest();
        }
        finally { _busy = false; _copyCancellation.Dispose(); _copyCancellation = null; UpdateActions(); }
    }

    private void ObserveExit()
    {
        var process = _desktop?.TestProcess ?? _normalDemo;
        if (process is null || !process.HasExited) return;
        var code = process.ExitCode;
        StatusText.Text = code == 0 ? "The app exited with code 0. Check its behavior and retained results before accepting it."
            : $"The app exited with error 0x{unchecked((uint)code):X8} ({code}). The copy and result are retained.";
        TrySave(new { stage = "exited", exitCode = code, testAccount = _desktop is not null });
        StopOwnedTest();
        UpdateActions();
    }

    private void ShowDesktop_Click(object? sender, RoutedEventArgs e) => ShowTestDesktop();
    private void ShowTestDesktop()
    {
        if (!OperatingSystem.IsWindows() || _desktop?.TestProcess is not { HasExited: false }) return;
        try { _desktop.Activate(); }
        catch (Exception error) { StatusText.Text = "Could not show the test desktop: " + error.Message; }
    }

    private void CancelTest_Click(object? sender, RoutedEventArgs e)
    {
        _copyCancellation?.Cancel();
        if (!_busy) StopOwnedTest();
        TrySave(new { stage = "canceled-by-user" });
        StatusText.Text = "Test canceled. Working copy and results are retained.";
        UpdateActions();
    }

    private void StopOwnedTest()
    {
        _monitor.Stop();
        try { _desktop?.Dispose(); }
        catch (Exception error) { _services.Log.Write("Windows test cleanup failed: " + error.Message); }
        finally { _desktop = null; }
        if (_normalDemo is not null)
        {
            try { if (!_normalDemo.HasExited) { _normalDemo.Kill(entireProcessTree: true); _normalDemo.WaitForExit(3000); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception error) { _services.Log.Write("Normal demo cleanup failed: " + error.Message); }
            finally { _normalDemo.Dispose(); _normalDemo = null; }
        }
    }

    private void TrySave(object value)
    {
        if (_copy is null) return;
        try { WindowsTestWorkspace.SaveResult(_copy.Directory, value); }
        catch (Exception error) { _services.Log.Write("Windows test result could not be saved: " + error.Message); }
    }

    private void UpdateActions()
    {
        var active = _desktop is not null || _normalDemo is not null;
        NormalDemoButton.IsEnabled = AccountDemoButton.IsEnabled = ChooseAppButton.IsEnabled = !_busy && !active;
        RunAppButton.IsEnabled = !_busy && !active && _program is not null;
        ShowDesktopButton.IsEnabled = !_busy && _desktop?.TestProcess is { HasExited: false };
        CancelTestButton.IsEnabled = _busy || active;
        OpenResultsButton.IsEnabled = _copy is not null;
    }

    private void OpenResults_Click(object? sender, RoutedEventArgs e)
    {
        if (_copy is not null) _services.Desktop.OpenProjectFolder(_copy.Directory);
    }
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

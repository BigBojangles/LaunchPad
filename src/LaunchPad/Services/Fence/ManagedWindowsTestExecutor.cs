using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LaunchPad.Services.Fence;

// Windows adapter. The protocol/ledger can be reused on Mac, but this account,
// job and separate desktop are not a claim of Mac protection or live GUI proof.
public sealed class ManagedWindowsTestExecutor : IWindowsTestExecutor
{
    private readonly Action<InteractiveTestDesktop?>? _interactiveReady;
    private readonly Func<bool>? _canStart;
    public ManagedWindowsTestExecutor(Action<InteractiveTestDesktop?>? interactiveReady = null, Func<bool>? canStart = null)
    { _interactiveReady = interactiveReady; _canStart = canStart; }

    public async Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string workingCopy, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return new("failed", null, "Managed Windows tests require Windows.");
        WindowsTestProtocol.Validate(request, request.Generation);
        if (!TestUserRunner.LaunchAccountReady()) return new("failed", null, "BuildLaunchTest is not ready. Use Windows setup/Repair setup.");
        if (request.Interactive && (request.Tool != "project" || _interactiveReady is null))
            return new("failed", null, "Interactive tests require a snapshot .exe and a host Show test desktop control.");
        var executable = ResolveTool(request, workingCopy);
        if (executable is null) return new("failed", null, "The requested Windows tool is not installed in a supported machine location. No command was started.");
        var cwd = workingCopy;
        if (request.WorkingDirectory != "." && (!FenceFiles.TryResolveUnlinked(workingCopy, request.WorkingDirectory, out cwd) || !Directory.Exists(cwd)))
            return new("failed", null, "The requested working directory is absent from the snapshot.");
        token.ThrowIfCancellationRequested();
        RestrictedRuntimeAccess.ModifyDirectory(workingCopy); // Only this new copy, never the host-owned parent/ledger.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            if (request.Interactive) return await RunInteractiveAsync(request, executable, cwd, deadline.Token, token).ConfigureAwait(false);
            return await RunCommandAsync(request, executable, cwd, workingCopy, deadline.Token, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(token.IsCancellationRequested ? "canceled" : "timed-out", null,
                "Windows test startup was interrupted. Its working copy is retained.", LogsTruncated: true);
        }
    }

    private static string? ResolveTool(WindowsTestRequest request, string workspace)
    {
        if (request.Tool == "project") return FenceFiles.TryResolveUnlinked(workspace, request.Program!, out var program) && File.Exists(program) ? program : null;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (request.Tool == "powershell")
        {
            var path = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(path) ? path : null;
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var relative = request.Tool switch { "dotnet" => "dotnet/dotnet.exe", "node" => "nodejs/node.exe", _ => null };
            if (relative is not null && FenceFiles.TryResolveUnlinked(root, relative, out var path) && File.Exists(path)) return path;
            if (request.Tool == "python")
                foreach (var directory in Directory.EnumerateDirectories(root, "Python*").OrderByDescending(path => path).Take(20))
                    if (FenceFiles.TryResolveUnlinked(directory, "python.exe", out var python) && File.Exists(python)) return python;
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private async Task<WindowsTestExecution> RunCommandAsync(WindowsTestRequest request, string executable, string cwd,
        string workspace, CancellationToken deadline, CancellationToken external)
    {
        var control = Path.Combine(workspace, ".launchpad-test");
        Directory.CreateDirectory(control);
        var runner = Path.Combine(control, "WindowsTestRunner.exe");
        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaunchPad.WindowsTestRunner.exe")
            ?? throw new IOException("The managed Windows test runner is absent from this build."))
        using (var output = new FileStream(runner, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { resource.CopyTo(output); output.Flush(true); }
        var command = Path.Combine(control, "command.bin");
        using (var file = new FileStream(command, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new BinaryWriter(file, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write("LaunchPad.WindowsTest.v1"); writer.Write(executable); writer.Write(cwd); writer.Write(request.Arguments.Length);
            foreach (var argument in request.Arguments) writer.Write(argument);
            writer.Flush(); file.Flush(true);
        }
        using var job = new TestJob();
        deadline.ThrowIfCancellationRequested();
        if (!TestUserRunner.TryStartManagedTest(runner, [command], workspace,
            (pid, handle) => (_canStart?.Invoke() ?? true) && job.Assign(pid, handle), deadline, out var process, out var error, out var failure))
            return new("failed", null, "Windows test startup failed (Windows error " + error + "): "
                + (string.IsNullOrWhiteSpace(failure) ? new Win32Exception(error).Message : failure));
        using (process)
        {
            var outcome = "finished";
            try { await process!.WaitForExitAsync(deadline).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                job.Cancel();
                outcome = external.IsCancellationRequested ? "canceled" : "timed-out";
                if (!process!.WaitForExit(3000)) return new("failed", null, "Owned Windows test cleanup did not finish; working copy retained.");
            }
            // Kill any remaining descendants before reading mutable test data.
            job.Cancel();
            if (!job.WaitEmpty(TimeSpan.FromSeconds(3))) return new("failed", outcome == "finished" ? process!.ExitCode : null, "Owned Windows test descendants are still stopping; copy retained.");
            var runnerError = Path.Combine(control, "runner-error.txt");
            if (File.Exists(runnerError) && outcome == "finished") return new("failed", process!.ExitCode, "Windows test runner could not complete. Its working copy is retained.");
            return new(outcome, outcome == "finished" ? process!.ExitCode : null,
                LogsTruncated: outcome != "finished" || File.Exists(Path.Combine(control, "logs-truncated")),
                StandardOutput: File.Exists(Path.Combine(control, "stdout.txt")) ? ".launchpad-test/stdout.txt" : null,
                StandardError: File.Exists(Path.Combine(control, "stderr.txt")) ? ".launchpad-test/stderr.txt" : null);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<WindowsTestExecution> RunInteractiveAsync(WindowsTestRequest request, string executable, string cwd,
        CancellationToken deadline, CancellationToken external)
    {
        using var desktop = await Task.Run(() => InteractiveTestDesktop.Create(300), CancellationToken.None).ConfigureAwait(false);
        try
        {
        deadline.ThrowIfCancellationRequested();
        var process = await Task.Run(() => desktop.StartProcess(executable, request.Arguments, cwd, _canStart), CancellationToken.None).ConfigureAwait(false);
        deadline.ThrowIfCancellationRequested();
        _interactiveReady!(desktop); // Host offers Show/Return/Cancel; never auto-switch on a guest request.
        var outcome = "finished";
        try { await process.WaitForExitAsync(deadline).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!desktop.Cancel(TimeSpan.FromSeconds(3))) return new("failed", null, "Owned interactive test cleanup remains unverified.");
            outcome = external.IsCancellationRequested ? "canceled" : "timed-out";
        }
        if (!desktop.Cancel(TimeSpan.FromSeconds(3))) return new("failed", null, "Owned interactive test descendants remain active.");
        if (desktop.GuardFailed) return new("failed", null, "The Windows test return controls failed; the working copy is retained.");
        if (desktop.CanceledByGuard) outcome = "canceled";
        if (!desktop.ReturnToLaunchPad()) return new("failed", outcome == "finished" && process.HasExited ? process.ExitCode : null, "Could not verify return to the normal desktop.");
        return new(outcome, outcome == "finished" && process.HasExited ? process.ExitCode : null);
        }
        finally { _interactiveReady?.Invoke(null); } // Detach controls before the owning desktop is disposed.
    }

    [SupportedOSPlatform("windows")]
    private sealed class TestJob : IDisposable
    {
        private nint _handle;
        public TestJob()
        {
            _handle = CreateJobObject(0, null);
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (_handle == 0 || !SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            { var error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
        }
        public bool Assign(int _, nint process) => AssignProcessToJobObject(_handle, process);
        public void Cancel() { if (_handle != 0 && !TerminateJobObject(_handle, 130)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public bool WaitEmpty(TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                if (!QueryInformationJobObject(_handle, 1, out var count, (uint)Marshal.SizeOf<Accounting>(), 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (count.Active == 0) return true;
                Thread.Sleep(20);
            } while (clock.Elapsed < timeout);
            return false;
        }
        public void Dispose() { if (_handle != 0) { CloseHandle(_handle); _handle = 0; } }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public ulong IoRead, IoWrite, IoOther, TransferRead, TransferWrite, TransferOther; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)] private struct Accounting { public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, Total, Active, Terminated; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int information, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int information, out Accounting accounting, uint length, nint returned);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    }
}

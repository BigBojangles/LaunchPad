using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

// Desktop/process primitive for the managed Windows test bridge. Not wired to
// guest requests yet. Return changes desktops; Cancel terminates only this job.
[SupportedOSPlatform("windows")]
public sealed class InteractiveTestDesktop : IDisposable, IWindowsTestDesktopControl
{
    public const string GuardArgument = "--test-desktop-watchdog";
    private sealed record GuardRequest(string Id, string Original, int OwnerPid, long OwnerStart, int VisitSeconds);
    private sealed record GuardHandles(string Id, long Job);
    public static bool IsGuardRequest(string[] args) => args.Length == 3 && args[0] == GuardArgument;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name => "LaunchPad-Test-" + Id;
    public string OriginalName { get; private set; } = "";
    internal nint Station { get; private set; }
    public nint Desktop { get; private set; }
    private nint _original, _job;
    private Process? _guard;
    private FileStream? _requestLease;
    private FileStream? _handlesLease;
    private string _guardRoot = "";
    private bool _activated, _disposed, _startAttempted;
    public Process? TestProcess { get; private set; }
    public bool Returned => TryInputName(out var name) && name == OriginalName;
    public bool CanceledByGuard => _guardRoot.Length != 0 && File.Exists(Path.Combine(_guardRoot, "canceled.json"));
    public bool GuardFailed => _guardRoot.Length != 0 && File.Exists(Path.Combine(_guardRoot, "guard-error.json"));

    private InteractiveTestDesktop() { }

    public static InteractiveTestDesktop Create(int maximumVisitSeconds = 30)
    {
        if (maximumVisitSeconds is < 2 or > 300) throw new ArgumentOutOfRangeException(nameof(maximumVisitSeconds));
        var lease = new InteractiveTestDesktop();
        try
        {
            lease.Station = GetProcessWindowStation();
            if (ObjectName(lease.Station) != "WinSta0") throw new Win32Exception(5, "An interactive Windows session is required.");
            lease._original = OpenInputDesktop(0, false, 0x100);
            if (lease._original == 0) Fail();
            lease.OriginalName = ObjectName(lease._original);
            if (lease.OriginalName != "Default") throw new Win32Exception(5, "Unlock and return to your ordinary desktop before Windows testing.");
            using var owner = WindowsIdentity.GetCurrent();
            var accountSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, TestUserRunner.UserName).Translate(typeof(SecurityIdentifier));
            var descriptorText = "D:P(A;;GA;;;SY)(A;;GA;;;" + owner.User!.Value + ")(A;;0x000000c7;;;" + accountSid.Value + ")(A;;0x000000c7;;;RC)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(descriptorText, 1, out var descriptor, out _)) Fail();
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                lease.Desktop = CreateDesktop(lease.Name, null, 0, 0, 0x1c7, ref attributes);
                if (lease.Desktop == 0) Fail();
            }
            finally { LocalFree(descriptor); }
            lease._job = CreateJobObject(0, null);
            var limits = new JobLimits { Basic = new() { Flags = 0x2000 } };
            if (lease._job == 0 || !SetInformationJobObject(lease._job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) Fail();
            lease.StartGuard(maximumVisitSeconds);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public Process StartProcess(string executable, IReadOnlyList<string> arguments, string workingCopy, Func<bool>? canStart = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_startAttempted) throw new InvalidOperationException("Create a new test desktop for another start attempt; the prior diagnostics are preserved.");
        _startAttempted = true;
        if (!TestUserRunner.TryStartInteractive(executable, arguments, workingCopy, this,
            (_, handle) => (canStart?.Invoke() ?? true) && AssignProcessToJobObject(_job, handle), out var process, out var error, out var failure))
            throw new Win32Exception(error, failure ?? "The Windows test could not start under the restricted test account.");
        TestProcess = process!;
        return TestProcess;
    }

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (TestProcess is null || _guard is null || _guard.HasExited) throw new InvalidOperationException("The test and return watchdog must be ready first.");
        if (InputName() != OriginalName) throw new Win32Exception(5, "The input desktop changed before Windows testing.");
        if (!SwitchDesktop(Desktop)) Fail();
        _activated = true;
        File.WriteAllText(Path.Combine(_guardRoot, "activated"), Id);
    }

    public bool ReturnToLaunchPad()
    {
        if (!_activated) return true;
        if (!TryInputName(out var current)) return false;
        if (current == Name && !SwitchDesktop(_original)) return false;
        return Returned;
    }

    public bool Cancel(TimeSpan timeout)
    {
        if (_job == 0) return true;
        if (!TerminateJobObject(_job, 130)) Fail();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (!QueryInformationJobObject(_job, 1, out var info, (uint)Marshal.SizeOf<JobAccounting>(), 0)) Fail();
            if (info.Active == 0) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private void StartGuard(int seconds)
    {
        _guardRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchPad", "test-desktops", Id);
        Directory.CreateDirectory(_guardRoot); // Host-owned; no guest/test-account write grant.
        using var owner = Process.GetCurrentProcess();
        var path = Path.Combine(_guardRoot, "request.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new GuardRequest(Id, OriginalName, owner.Id, owner.StartTime.ToUniversalTime().Ticks, seconds)));
        _requestLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_requestLease));
        var helper = Path.Combine(AppContext.BaseDirectory, typeof(InteractiveTestDesktop).Assembly.GetName().Name + ".exe");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in new[] { GuardArgument, path, hash }) start.ArgumentList.Add(argument);
        _guard = Process.Start(start) ?? throw new Win32Exception(5, "The return watchdog did not start.");
        if (!DuplicateHandle(GetCurrentProcess(), _job, _guard.Handle, out var guardJob, 0x0c, false, 0)) Fail();
        var handlesPath = Path.Combine(_guardRoot, "handles.json");
        File.WriteAllText(handlesPath, JsonSerializer.Serialize(new GuardHandles(Id, guardJob.ToInt64())));
        _handlesLease = new FileStream(handlesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clock = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(_guardRoot, "ready")))
        {
            if (_guard.HasExited || clock.Elapsed > TimeSpan.FromSeconds(10)) throw new Win32Exception(1460, "The return watchdog did not become ready.");
            Thread.Sleep(20);
        }
        if (File.ReadAllText(Path.Combine(_guardRoot, "ready")) != Id) throw new Win32Exception(13, "Return watchdog identity mismatch.");
    }

    public static int RunGuard(string[] args)
    {
        if (!IsGuardRequest(args)) return 50;
        nint original = 0, testDesktop = 0, job = 0;
        string? reportRoot = null;
        try
        {
            var path = Path.GetFullPath(args[1]);
            var root = Path.GetDirectoryName(path)!;
            var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchPad", "test-desktops");
            if (Path.GetDirectoryName(root) != allowed || !Guid.TryParseExact(Path.GetFileName(root), "N", out _)) return 13;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4096 || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(args[2], StringComparison.Ordinal)) return 13;
            stream.Position = 0;
            var request = JsonSerializer.Deserialize<GuardRequest>(stream) ?? throw new InvalidDataException();
            if (request.Id != Path.GetFileName(root) || request.Original != "Default" || request.VisitSeconds is < 2 or > 300) return 13;
            reportRoot = root;
            using var owner = Process.GetProcessById(request.OwnerPid);
            if (owner.StartTime.ToUniversalTime().Ticks != request.OwnerStart) return 13;
            _ = owner.Handle; // Keep exit observation bound to this process.
            var wait = Stopwatch.StartNew();
            var handlesPath = Path.Combine(root, "handles.json");
            while (!File.Exists(handlesPath))
            {
                if (owner.HasExited || wait.Elapsed > TimeSpan.FromSeconds(10)) throw new Win32Exception(1460, "Watchdog job handoff timed out.");
                Thread.Sleep(20);
            }
            using var handlesStream = new FileStream(handlesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (handlesStream.Length > 4096) throw new InvalidDataException();
            var handles = JsonSerializer.Deserialize<GuardHandles>(handlesStream) ?? throw new InvalidDataException();
            if (handles.Id != request.Id) throw new InvalidDataException();
            job = (nint)handles.Job;
            if (!QueryInformationJobObject(job, 1, out _, (uint)Marshal.SizeOf<JobAccounting>(), 0)) Fail();
            original = OpenDesktop(request.Original, 0, false, 0x100);
            if (original == 0) Fail();
            testDesktop = OpenDesktop("LaunchPad-Test-" + request.Id, 0, false, 0x1c7);
            if (testDesktop == 0) Fail();
            using var controls = TestDesktopControls.Start(testDesktop);
            File.WriteAllText(Path.Combine(root, "controls.json"), JsonSerializer.Serialize(new
            { pid = Environment.ProcessId, window = controls.WindowHandle.ToInt64(), desktop = "LaunchPad-Test-" + request.Id }));
            File.WriteAllText(Path.Combine(root, "ready"), request.Id);
            Stopwatch? visit = null;
            var returnRequested = false;
            var cancelRequested = false;
            var cancelIssued = false;
            string? trigger = null;
            while (true)
            {
                var inputAvailable = TryInputName(out var current);
                // Observe the actual input desktop: a failed activation or a
                // lock/unrelated desktop must not become a successful visit.
                if (visit is null && inputAvailable && current == "LaunchPad-Test-" + request.Id) visit = Stopwatch.StartNew();
                var ownerExited = owner.HasExited;
                cancelRequested |= ownerExited || controls.CancelRequested || !controls.IsAlive;
                if (cancelRequested && !cancelIssued)
                {
                    File.WriteAllText(Path.Combine(root, "canceled.json"), JsonSerializer.Serialize(new
                    { id = request.Id, ownerExited, cancelButton = controls.CancelRequested, controlsFailed = !controls.IsAlive }));
                    if (!TerminateJobObject(job, 130)) Fail();
                    cancelIssued = true;
                }
                if (ownerExited) trigger ??= "owner-exit";
                else if (!controls.IsAlive) trigger ??= "controls-failed";
                else if (controls.CancelRequested) trigger ??= "cancel-button";
                else if (controls.ReturnRequested) trigger ??= "return-button";
                else if (File.Exists(Path.Combine(root, "stop"))) trigger ??= "owner-cleanup";
                else if (visit is not null && visit.Elapsed > TimeSpan.FromSeconds(request.VisitSeconds)) trigger ??= "visit-deadline";
                returnRequested |= trigger is not null;
                if (returnRequested && inputAvailable)
                {
                    if (current == "LaunchPad-Test-" + request.Id) SwitchDesktop(original);
                    if (TryInputName(out var after) && after == request.Original)
                    {
                        File.WriteAllText(Path.Combine(root, "returned.json"), JsonSerializer.Serialize(new
                        { original = request.Original, input = after, ownerExited, activationObserved = visit is not null, trigger, cancelIssued }));
                        return 0;
                    }
                }
                // Locked/disconnected/unavailable or externally switched
                // desktops leave the independent watchdog armed until Default
                // is actually observed. It never attempts to unlock Windows.
                Thread.Sleep(50);
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            if (reportRoot is not null)
                try { File.WriteAllText(Path.Combine(reportRoot, "guard-error.json"), JsonSerializer.Serialize(new { error = error.Message })); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (job != 0) TerminateJobObject(job, 130);
            if (original != 0 && TryInputName(out var current) && current == "LaunchPad-Test-" + Path.GetFileName(reportRoot)) SwitchDesktop(original);
            return 1;
        }
        finally
        {
            if (job != 0) CloseHandle(job);
            if (testDesktop != 0) CloseDesktop(testDesktop);
            if (original != 0) CloseDesktop(original);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Keep the independent watchdog armed if returning fails; its bounded
        // deadline/owner-exit path remains separate from test-app code.
        var returned = false;
        var failures = new List<string>();
        try { returned = !_activated || ReturnToLaunchPad(); }
        catch (Win32Exception error) { failures.Add("return: " + error.Message); }
        try { if (!Cancel(TimeSpan.FromSeconds(5))) failures.Add("cancel: owned job did not empty before cleanup timeout"); }
        catch (Win32Exception error) { failures.Add("cancel: " + error.Message); }
        try
        {
            if (_guardRoot.Length != 0)
            {
                // Request cleanup even while locked; the guard only exits
                // after observing Default. Its retained job/desktop handles
                // survive disposal or owner death.
                File.WriteAllText(Path.Combine(_guardRoot, "stop"), Id);
                File.WriteAllText(Path.Combine(_guardRoot, "cleanup.json"), JsonSerializer.Serialize(new { returned, failures }));
            }
            _guard?.WaitForExit(3000);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        finally
        {
            TestProcess?.Dispose();
            _guard?.Dispose();
            _handlesLease?.Dispose();
            _requestLease?.Dispose();
            if (_job != 0) CloseHandle(_job);
            if (Desktop != 0) CloseDesktop(Desktop);
            if (_original != 0) CloseDesktop(_original);
            _job = Desktop = _original = 0;
            _disposed = true;
        }
    }

    public static string InputName()
    {
        var input = OpenInputDesktop(0, false, 0x100);
        if (input == 0) Fail();
        try { return ObjectName(input); }
        finally { CloseDesktop(input); }
    }
    private static bool TryInputName(out string name)
    {
        try { name = InputName(); return true; }
        catch (Win32Exception) { name = ""; return false; }
    }
    private static string ObjectName(nint handle)
    {
        var name = new StringBuilder(512);
        if (!GetUserObjectInformation(handle, 2, name, name.Capacity * 2, out _)) Fail();
        return name.ToString();
    }
    private static void Fail() => throw new Win32Exception(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting { public long UserTime, KernelTime, PeriodUser, PeriodKernel; public uint Faults, Total, Active, Terminated; }
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string name, string? device, nint mode, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SwitchDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int information, StringBuilder text, int bytes, out int required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint bytes);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int kind, ref JobLimits limits, uint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int kind, out JobAccounting limits, uint bytes, nint required);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exit);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(nint source, nint handle, nint target, out nint copy, uint access, bool inherit, uint options);
}

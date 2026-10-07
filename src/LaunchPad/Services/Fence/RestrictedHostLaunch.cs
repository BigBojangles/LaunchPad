using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

// Trusted bootstrap only. The requested executable is never run using its
// unrestricted token. The parent receives verified native child handles while
// that child is suspended, then assigns its ordinary terminal-owned job.
public static class RestrictedHostLaunch
{
    public const string Argument = "--restricted-host-launch";
    public static bool IsRequest(string[] args) => args.Length == 3 && args[0] == Argument;
    private sealed record Request(string Nonce, string Executable, string CommandLine, string Directory,
        int ParentPid, long ParentStart, string ParentSid, bool InteractiveTest = false, bool ManagedTest = false);
    private sealed record DesktopHandles(string Nonce, long Station, long Desktop);
    private sealed record Reply(string Nonce, int ProcessId, long ProcessHandle, long ThreadHandle,
        long StationHandle, long DesktopHandle, int Error, string? Message);
    private sealed record Group(string Sid, uint Attributes);
    private const uint LogonGroup = 0xc0000000;
    private const uint Enabled = 4;
    private const uint DenyOnly = 16;
    // The account SID must not be a restricting SID: that would authorize the
    // unrestricted bootstrap's account-owned objects in both access checks.
    // Standard shared OS objects use Everyone, Authenticated Users or BUILTIN\Users. These
    // restrictors retain their existing grants; they add no file/system ACEs.
    internal static readonly string[] RestrictingSids = ["S-1-1-0", "S-1-5-11", "S-1-5-12", "S-1-5-32-545"];

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsRequest(args)) return 50;
        return RunWindows(args);
    }

    [SupportedOSPlatform("windows")]
    internal static bool Start(string exe, string command, string directory, string password,
        Func<int, nint, bool> park, out Process? process, out int error)
        => StartCore(exe, command, directory, password, park, null, false, CancellationToken.None, out process, out error, out _);

    [SupportedOSPlatform("windows")]
    internal static bool StartManagedTest(string exe, string command, string directory, string password,
        Func<int, nint, bool> park, CancellationToken token, out Process? process, out int error, out string? failure)
        => StartCore(exe, command, directory, password, park, null, true, token, out process, out error, out failure);

    [SupportedOSPlatform("windows")]
    internal static bool StartInteractive(string exe, string command, string directory, string password,
        Func<int, nint, bool> park, InteractiveTestDesktop desktop, out Process? process, out int error, out string? failure)
        => StartCore(exe, command, directory, password, park, desktop, false, CancellationToken.None, out process, out error, out failure);

    [SupportedOSPlatform("windows")]
    private static bool StartCore(string exe, string command, string directory, string password,
        Func<int, nint, bool> park, InteractiveTestDesktop? testDesktop, bool managedTest, CancellationToken cancellation,
        out Process? process, out int error, out string? failure)
    {
        process = null;
        error = 0;
        failure = null;
        // BaseDirectory remains the apphost directory in a single-file publish;
        // Assembly.Location is empty there. It also resolves the copied apphost
        // beside LaunchPad.dll when these services run inside the test runner.
        var helper = Path.Combine(AppContext.BaseDirectory, typeof(RestrictedHostLaunch).Assembly.GetName().Name + ".exe");
        if (!File.Exists(helper)) { error = 2; failure = "The restricted launch helper is absent from the app runtime."; return false; }
        var nonce = testDesktop?.Id ?? Guid.NewGuid().ToString("N");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) { error = 3; failure = "The host account's local application-data directory is unavailable."; return false; }
        var root = Path.Combine(local, "LaunchPad", "host-launch", nonce);
        nint bootstrapJob = 0, childProcess = 0, childThread = 0, station = 0, desktop = 0;
        var bootstrap = new ProcessInformation();
        var committed = false;
        var stage = "prepare launch request";
        FileStream? desktopLease = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);
            Grant(root);
            using var owner = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent();
            var request = new Request(nonce, Path.GetFullPath(exe), command, Path.GetFullPath(directory),
                owner.Id, owner.StartTime.ToUniversalTime().Ticks, identity.User!.Value, testDesktop is not null, managedTest);
            var requestPath = Path.Combine(root, "request.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
            // Keep request bytes immutable until the helper/child handoff ends.
            using var lease = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(lease)).ToLowerInvariant();
            var helperCommand = new StringBuilder(Quote(helper) + " " + Argument + " " + Quote(requestPath) + " " + requestHash);
            var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            stage = "start restricted launch helper";
            if (!CreateProcessWithLogonW(TestUserRunner.UserName, ".", password, 1, helper, helperCommand,
                0x08000004, 0, Path.GetDirectoryName(helper)!, ref si, out bootstrap)) ThrowLast();
            if (testDesktop is not null)
            {
                stage = "transfer test desktop handles";
                // Duplicate limited existing handles without changing WinSta0's
                // DACL or granting clipboard/screen access or Default desktop.
                if (!DuplicateHandle(GetCurrentProcess(), testDesktop.Station, bootstrap.Process, out var remoteStation, 0x22, false, 0)) ThrowLast();
                if (!DuplicateHandle(GetCurrentProcess(), testDesktop.Desktop, bootstrap.Process, out var remoteDesktop, 0xc7, false, 0)) ThrowLast();
                var handlesPath = Path.Combine(root, "desktop-handles.json");
                File.WriteAllText(handlesPath, JsonSerializer.Serialize(new DesktopHandles(nonce, remoteStation.ToInt64(), remoteDesktop.ToInt64())));
                desktopLease = new FileStream(handlesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            stage = "assign launch helper job";
            bootstrapJob = CreateJobObject(0, null);
            if (bootstrapJob == 0 || !SetBootstrapKill(bootstrapJob, true) || !AssignProcessToJobObject(bootstrapJob, bootstrap.Process)) ThrowLast();
            stage = "resume launch helper";
            if (ResumeThread(bootstrap.Thread) == uint.MaxValue) ThrowLast();
            stage = "wait for restricted child receipt";
            var replyPath = Path.Combine(root, "reply.json");
            var clock = Stopwatch.StartNew();
            while (!File.Exists(replyPath))
            {
                cancellation.ThrowIfCancellationRequested();
                if (WaitForSingleObject(bootstrap.Process, 0) == 0) throw new Win32Exception(1067, "Restricted bootstrap exited before replying.");
                if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new Win32Exception(1460, "Restricted bootstrap did not become ready.");
                Thread.Sleep(20);
            }
            stage = "validate restricted child receipt";
            var reply = ReadJson<Reply>(replyPath);
            if (reply.Nonce != nonce) throw new Win32Exception(13, "Restricted launch receipt mismatch.");
            if (reply.Error != 0) throw new Win32Exception(reply.Error, reply.Message);
            stage = "validate restricted child identity";
            if (!DuplicateHandle(bootstrap.Process, (nint)reply.ProcessHandle, GetCurrentProcess(), out childProcess, 0, false, 2)
                || !DuplicateHandle(bootstrap.Process, (nint)reply.ThreadHandle, GetCurrentProcess(), out childThread, 0, false, 2)) ThrowLast();
            if (GetProcessId(childProcess) != reply.ProcessId || GetProcessIdOfThread(childThread) != reply.ProcessId)
                throw new Win32Exception(13, "Restricted child handle identity mismatch.");
            var image = new StringBuilder(32768);
            var imageSize = image.Capacity;
            if (!QueryFullProcessImageName(childProcess, 0, image, ref imageSize)) ThrowLast();
            if (!Path.GetFullPath(image.ToString()).Equals(request.Executable, StringComparison.OrdinalIgnoreCase))
                throw new Win32Exception(13, "Restricted child executable mismatch.");
            VerifyChildToken(childProcess);
            stage = "validate restricted desktop handles";
            if (!DuplicateHandle(bootstrap.Process, (nint)reply.StationHandle, GetCurrentProcess(), out station, 0, false, 2)
                || !DuplicateHandle(bootstrap.Process, (nint)reply.DesktopHandle, GetCurrentProcess(), out desktop, 0, false, 2)) ThrowLast();
            if (UserObjectText(station, 3) != "WindowStation"
                || (testDesktop is null ? UserObjectText(station, 2) == "WinSta0" : UserObjectText(station, 2) != "WinSta0")
                || UserObjectText(desktop, 3) != "Desktop" || UserObjectText(desktop, 2) != (testDesktop?.Name ?? "LaunchPad-" + nonce))
                throw new Win32Exception(13, "Restricted desktop handle identity mismatch.");
            // The child must hold these objects before its first user32 call;
            // otherwise the short-lived bootstrap can close the last reference.
            if (!DuplicateHandle(GetCurrentProcess(), station, childProcess, out _, 0, false, 2)
                || !DuplicateHandle(GetCurrentProcess(), desktop, childProcess, out _, 0, false, 2)) ThrowLast();
            stage = "assign restricted child job";
            process = Process.GetProcessById(reply.ProcessId);
            _ = process.StartTime; // Bind lifecycle identity before allowing execution.
            _ = process.Handle; // Retain the native lifecycle handle through a short-lived child exit.
            cancellation.ThrowIfCancellationRequested();
            if (!park(reply.ProcessId, childProcess)) throw new Win32Exception(5, "Restricted child job assignment failed.");
            if (!SetBootstrapKill(bootstrapJob, false)) ThrowLast();
            cancellation.ThrowIfCancellationRequested();
            stage = "resume restricted child";
            if (ResumeThread(childThread) != 1) throw new Win32Exception(13, "Restricted child was not suspended.");
            stage = "commit restricted launch";
            File.WriteAllText(Path.Combine(root, "committed"), nonce);
            committed = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            process?.Dispose();
            process = null;
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or ArgumentException or System.Security.SecurityException)
        {
            error = exception is Win32Exception native ? native.NativeErrorCode : 13;
            failure = stage + ": " + exception.Message;
            // Keep paths in the private owned receipt, never credentials or command text.
            try { File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { error, stage, exception.Message, helper, helperDirectory = Path.GetDirectoryName(helper) })); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            process?.Dispose();
            process = null;
            return false;
        }
        finally
        {
            if (!committed)
            {
                if (bootstrapJob != 0) TerminateJobObject(bootstrapJob, 1);
                else if (bootstrap.Process != 0) TerminateProcess(bootstrap.Process, 1);
                if (childProcess != 0) { TerminateProcess(childProcess, 1); TestUserRunner.ReleaseMachine(GetProcessId(childProcess)); }
            }
            if (childThread != 0) CloseHandle(childThread);
            if (childProcess != 0) CloseHandle(childProcess);
            if (desktop != 0) CloseDesktop(desktop);
            if (station != 0) CloseWindowStation(station);
            if (bootstrap.Thread != 0) CloseHandle(bootstrap.Thread);
            if (bootstrap.Process != 0) CloseHandle(bootstrap.Process);
            if (bootstrapJob != 0) CloseHandle(bootstrapJob);
            desktopLease?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        Request? request = null;
        ProcessInformation child = new();
        nint token = 0, restricted = 0, station = 0, desktop = 0, descriptor = 0;
        nint inheritedStation = 0, inheritedDesktop = 0;
        var root = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
        var committed = false;
        var stage = "validate request";
        WindowsTestEnvironment? childEnvironment = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!identity.Name.EndsWith("\\" + TestUserRunner.UserName, StringComparison.OrdinalIgnoreCase)
                || principal.IsInRole(WindowsBuiltInRole.Administrator)) throw new Win32Exception(5, "Standard launch identity required.");
            using (var lease = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (lease.Length > 65536) throw new Win32Exception(13, "Oversized restricted launch request.");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(lease)).ToLowerInvariant();
                if (hash != args[2]) throw new Win32Exception(13, "Restricted request identity mismatch.");
                lease.Position = 0;
                request = JsonSerializer.Deserialize<Request>(lease) ?? throw new Win32Exception(13);
            }
            if (request.Nonce != Path.GetFileName(root) || !Guid.TryParseExact(request.Nonce, "N", out _)) throw new Win32Exception(13);
            using (var parent = Process.GetProcessById(request.ParentPid))
                if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStart) throw new Win32Exception(13, "Restricted parent identity changed.");
            var parentHandle = OpenProcess(0x1000, false, request.ParentPid);
            if (parentHandle == 0) ThrowLast();
            try
            {
                if (!OpenProcessToken(parentHandle, 8, out var parentToken)) ThrowLast();
                try
                {
                    using var parentIdentity = new WindowsIdentity(parentToken);
                    if (parentIdentity.User?.Value != request.ParentSid) throw new Win32Exception(13, "Restricted parent SID mismatch.");
                }
                finally { CloseHandle(parentToken); }
            }
            finally { CloseHandle(parentHandle); }
            if (request.InteractiveTest && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                // The app entry thread is STA and may already own COM windows.
                // A fresh MTA thread has no windows/hooks when selecting the
                // owned test desktop. Revalidate the immutable request there.
                var workerResult = 13;
                var worker = new Thread(() => workerResult = RunWindows(args)) { Name = "LaunchPad restricted test startup" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
                worker.Join();
                return workerResult;
            }
            stage = "prepare restricted token";
            if (!OpenProcessToken(GetCurrentProcess(), 0x6008b, out token)) ThrowLast();
            var logons = Groups(token).Where(group => (group.Attributes & LogonGroup) == LogonGroup).ToArray();
            if (logons.Length == 0) throw new Win32Exception(13, "Logon group unavailable.");
            var sids = logons.Select(group => new SecurityIdentifier(group.Sid))
                .Concat(RestrictingSids.Select(sid => new SecurityIdentifier(sid))).ToArray();
            var allocations = sids.Select(sid => { var pointer = Marshal.AllocHGlobal(sid.BinaryLength); var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0); Marshal.Copy(bytes, 0, pointer, bytes.Length); return pointer; }).ToArray();
            try
            {
                var disabled = allocations.Take(logons.Length).Select(pointer => new SidAndAttributes { Sid = pointer }).ToArray();
                var restrictors = allocations.Skip(logons.Length).Select(pointer => new SidAndAttributes { Sid = pointer }).ToArray();
                // Full restriction, including reads. Neither WRITE_RESTRICTED
                // nor SANDBOX_INERT is used.
                if (!CreateRestrictedToken(token, 1, (uint)disabled.Length, disabled, 0, 0,
                    (uint)restrictors.Length, restrictors, out restricted)) ThrowLast();
            }
            finally { foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer); }
            VerifyLogons(restricted, logons.Select(group => group.Sid));
            VerifyRestrictors(restricted);
            // Separate noninteractive station/desktop, with explicit account
            // ACLs; CreateWindowStation's default would grant every user access.
            var sddl = "D:(A;;GA;;;SY)(A;;GA;;;RC)(A;;GA;;;" + identity.User!.Value + ")(A;;GA;;;" + request.ParentSid + ")";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, out _)) ThrowLast();
            if (!GetSecurityDescriptorDacl(descriptor, out var present, out var dacl, out _) || !present || dacl == 0)
                throw new Win32Exception(13, "Restricted object ACL unavailable.");
            var defaultDacl = new TokenDefaultDacl { Dacl = dacl };
            if (!SetTokenInformation(restricted, 6, ref defaultDacl, Marshal.SizeOf<TokenDefaultDacl>())) ThrowLast();
            var tokenAclError = SetSecurityInfo(restricted, 6, 4, 0, 0, dacl, 0);
            if (tokenAclError != 0) throw new Win32Exception((int)tokenAclError);
            // Apply only to the new restricted token. The unrestricted helper's
            // process/token/default ACL remains unchanged and has no RC grant.
            var temporary = Path.Combine(root, "temp");
            Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            if (request.InteractiveTest)
            {
                var handles = ReadJson<DesktopHandles>(Path.Combine(root, "desktop-handles.json"));
                if (handles.Nonce != request.Nonce) throw new Win32Exception(13, "Test desktop lease identity mismatch.");
                station = (nint)handles.Station;
                desktop = (nint)handles.Desktop;
                if (UserObjectText(station, 3) != "WindowStation" || UserObjectText(station, 2) != "WinSta0"
                    || UserObjectText(desktop, 3) != "Desktop" || UserObjectText(desktop, 2) != "LaunchPad-Test-" + request.Nonce)
                    throw new Win32Exception(13, "Unexpected test desktop handles.");
            }
            else
                station = CreateWindowStation(null, 0, 0x000f037f, ref attributes);
            stage = "select window station";
            if (station == 0 || !SetProcessWindowStation(station)) ThrowLast();
            var stationName = new StringBuilder(256);
            if (!GetUserObjectInformation(station, 2, stationName, stationName.Capacity * 2, out _)) ThrowLast();
            var desktopName = (request.InteractiveTest ? "LaunchPad-Test-" : "LaunchPad-") + request.Nonce;
            if (!request.InteractiveTest) desktop = CreateDesktop(desktopName, null, 0, 0, 0x000f01ff, ref attributes);
            if (desktop == 0) ThrowLast();
            if (request.InteractiveTest)
            {
                stage = "select test desktop";
                if (!SetThreadDesktop(desktop)) ThrowLast();
                // Test failures belong in retained results rather than modal
                // system error boxes. Only this short-lived helper/child use it.
                SetErrorMode(0x8003);
                // Process-local diagnostics for this preview child only. The
                // path is inside its already granted disposable app copy.
                // Genuine CreateProcess inheritance supplies the sole owned
                // pair before USER32 initialization. Post-creation handle
                // duplication is not a substitute for that startup contract.
                stage = "prepare inherited desktop handles";
                if (!DuplicateHandle(GetCurrentProcess(), station, GetCurrentProcess(), out inheritedStation, 0x22, true, 0)
                    || !DuplicateHandle(GetCurrentProcess(), desktop, GetCurrentProcess(), out inheritedDesktop, 0xc7, true, 0)) ThrowLast();
            }
            if (request.InteractiveTest || request.ManagedTest)
            {
                // Explicit test requests use the account's profile/environment.
                // Ordinary QEMU launches retain their existing behavior.
                SetErrorMode(0x8003);
                stage = "prepare test-account environment";
                var startupLogs = Path.Combine(request.Directory, "launchpad-startup-logs");
                Directory.CreateDirectory(startupLogs);
                childEnvironment = WindowsTestEnvironment.ForUser(token, temporary, startupLogs);
                if (!childEnvironment.UserName.Equals(TestUserRunner.UserName, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(childEnvironment.UserProfile))
                    throw new Win32Exception(13, "The loaded test-account profile/environment is unavailable.");
                File.WriteAllText(Path.Combine(root, "startup-context.json"), JsonSerializer.Serialize(new
                { account = childEnvironment.UserName, profile = childEnvironment.UserProfile, inheritedCallerEnvironment = false }));
            }
            var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = request.InteractiveTest ? null : stationName + "\\" + desktopName };
            var command = new StringBuilder(request.CommandLine);
            stage = "create restricted app";
            if (!CreateProcessAsUser(restricted, request.Executable, command, ref attributes, ref attributes, request.InteractiveTest,
                childEnvironment is not null ? 0x08000404u : 0x08000004u, childEnvironment?.Block ?? 0, request.Directory, ref si, out child)) ThrowLast();
            VerifyChildToken(child.Process);
            WriteReply(root, new Reply(request.Nonce, child.ProcessId, child.Process.ToInt64(), child.Thread.ToInt64(),
                station.ToInt64(), desktop.ToInt64(), 0, null));
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(25))
            {
                var ack = Path.Combine(root, "committed");
                if (File.Exists(ack) && File.ReadAllText(ack) == request.Nonce) { committed = true; return 0; }
                Thread.Sleep(20);
            }
            throw new Win32Exception(1460, "Restricted child handoff timed out.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or ArgumentException or System.Security.SecurityException)
        {
            var error = exception is Win32Exception native ? native.NativeErrorCode : 13;
            var message = request?.InteractiveTest == true ? $"Windows testing startup failed at {stage}: {exception.Message}" : exception.Message;
            WriteReply(root, new Reply(request?.Nonce ?? Path.GetFileName(root), 0, 0, 0, 0, 0, error, message));
            return error;
        }
        finally
        {
            childEnvironment?.Dispose();
            if (!committed && child.Process != 0) TerminateProcess(child.Process, 1);
            if (child.Thread != 0) CloseHandle(child.Thread);
            if (child.Process != 0) CloseHandle(child.Process);
            if (inheritedDesktop != 0) CloseDesktop(inheritedDesktop);
            if (inheritedStation != 0) CloseWindowStation(inheritedStation);
            if (desktop != 0) CloseDesktop(desktop);
            if (station != 0) CloseWindowStation(station);
            if (descriptor != 0) LocalFree(descriptor);
            if (restricted != 0) CloseHandle(restricted);
            if (token != 0) CloseHandle(token);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyChildToken(nint process)
    {
        if (!OpenProcessToken(process, 8, out var token)) ThrowLast();
        try
        {
            using var identity = new WindowsIdentity(token);
            var groups = Groups(token);
            if (!identity.Name.EndsWith("\\" + TestUserRunner.UserName, StringComparison.OrdinalIgnoreCase)
                || groups.Any(group => group.Sid == "S-1-5-32-544" && (group.Attributes & Enabled) != 0))
                throw new Win32Exception(5, "Restricted child identity mismatch.");
            var logons = groups.Where(group => (group.Attributes & LogonGroup) == LogonGroup).ToArray();
            if (logons.Length == 0) throw new Win32Exception(13, "Restricted child logon metadata absent.");
            VerifyLogons(token, logons.Select(group => group.Sid));
            VerifyRestrictors(token);
        }
        finally { CloseHandle(token); }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyLogons(nint token, IEnumerable<string> expected)
    {
        var groups = Groups(token);
        foreach (var sid in expected)
        {
            var group = groups.SingleOrDefault(group => group.Sid == sid);
            if (group is null || (group.Attributes & DenyOnly) == 0 || (group.Attributes & Enabled) != 0)
                throw new Win32Exception(13, "Logon grant remained enabled.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRestrictors(nint token)
    {
        var actual = Groups(token, 11).Select(group => group.Sid).OrderBy(sid => sid, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(RestrictingSids.OrderBy(sid => sid, StringComparer.Ordinal)))
            throw new Win32Exception(13, "Restricted SID list mismatch.");
    }

    [SupportedOSPlatform("windows")]
    private static Group[] Groups(nint token, int information = 2)
    {
        GetTokenInformation(token, information, 0, 0, out var bytes);
        if (bytes <= 0 || bytes > 1048576) throw new Win32Exception(13, "Invalid token group size.");
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!GetTokenInformation(token, information, buffer, bytes, out _)) ThrowLast();
            var count = Marshal.ReadInt32(buffer);
            var start = IntPtr.Size == 8 ? 8 : 4;
            var stride = Marshal.SizeOf<SidAndAttributes>();
            if (count < 0 || count > (bytes - start) / stride) throw new Win32Exception(13, "Invalid token group count.");
            return Enumerable.Range(0, count).Select(index =>
            {
                var entry = Marshal.PtrToStructure<SidAndAttributes>(buffer + start + index * stride);
                return new Group(new SecurityIdentifier(entry.Sid).Value, entry.Attributes);
            }).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static T ReadJson<T>(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 65536) throw new Win32Exception(13, "Oversized restricted launch receipt.");
        return JsonSerializer.Deserialize<T>(file) ?? throw new Win32Exception(13);
    }
    private static void WriteReply(string root, Reply reply)
    {
        var temporary = Path.Combine(root, "reply-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(reply));
        File.Move(temporary, Path.Combine(root, "reply.json"), overwrite: true);
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static string UserObjectText(nint handle, int information)
    {
        var text = new StringBuilder(512);
        if (!GetUserObjectInformation(handle, information, text, text.Capacity * 2, out _)) ThrowLast();
        return text.ToString();
    }
    private static void ThrowLast() => throw new Win32Exception(Marshal.GetLastWin32Error());
    private static void Grant(string root)
    {
        var info = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new Win32Exception(5);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new Win32Exception(1460); }
        if (process.ExitCode != 0) throw new Win32Exception(5, errors.Result + output.Result);
    }
    private static bool SetBootstrapKill(nint job, bool enabled)
    {
        var info = new JobLimits();
        info.Basic.Flags = enabled ? 0x2000u : 0;
        return SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JobLimits>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenDefaultDacl { public nint Dacl; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public nint ReservedPtr, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum;
        public uint Active; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public BasicLimits Basic; public ulong IoRead, IoWrite, IoOther, TransferRead, TransferWrite, TransferOther;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithLogonW(string user, string domain, string password, uint logonFlags, string exe, StringBuilder command, uint creationFlags, nint environment, string directory, ref StartupInfo info, out ProcessInformation process);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(nint token, string exe, StringBuilder command, ref SecurityAttributes processAttributes, ref SecurityAttributes threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfo info, out ProcessInformation process);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int information, nint buffer, int bytes, out int required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CreateRestrictedToken(nint token, uint flags, uint count, [In] SidAndAttributes[] disabled, uint privilegeCount, nint privileges, uint restrictedCount, [In] SidAndAttributes[] restricted, out nint result);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(nint token, int information, ref TokenDefaultDacl value, int bytes);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(nint handle, uint type, uint information, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(nint source, nint handle, nint target, out nint copy, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern int GetProcessId(nint process);
    [DllImport("kernel32.dll")] private static extern int GetProcessIdOfThread(nint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int information, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowStation(string? name, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int information, StringBuilder text, int bytes, out int required);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string name, string? device, nint mode, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(nint station);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}

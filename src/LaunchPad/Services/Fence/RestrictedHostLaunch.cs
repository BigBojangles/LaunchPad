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
        int ParentPid, long ParentStart, string ParentSid);
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
    {
        process = null;
        error = 0;
        // BaseDirectory remains the apphost directory in a single-file publish;
        // Assembly.Location is empty there. It also resolves the copied apphost
        // beside LaunchPad.dll when these services run inside the test runner.
        var helper = Path.Combine(AppContext.BaseDirectory, typeof(RestrictedHostLaunch).Assembly.GetName().Name + ".exe");
        if (!File.Exists(helper)) { error = 2; return false; }
        var nonce = Guid.NewGuid().ToString("N");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) { error = 3; return false; }
        var root = Path.Combine(local, "LaunchPad", "host-launch", nonce);
        nint bootstrapJob = 0, childProcess = 0, childThread = 0, station = 0, desktop = 0;
        var bootstrap = new ProcessInformation();
        var committed = false;
        try
        {
            Directory.CreateDirectory(root);
            Grant(root);
            using var owner = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent();
            var request = new Request(nonce, Path.GetFullPath(exe), command, Path.GetFullPath(directory),
                owner.Id, owner.StartTime.ToUniversalTime().Ticks, identity.User!.Value);
            var requestPath = Path.Combine(root, "request.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
            // Keep request bytes immutable until the helper/child handoff ends.
            using var lease = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(lease)).ToLowerInvariant();
            var helperCommand = new StringBuilder(Quote(helper) + " " + Argument + " " + Quote(requestPath) + " " + requestHash);
            var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessWithLogonW(TestUserRunner.UserName, ".", password, 1, helper, helperCommand,
                0x08000004, 0, Path.GetDirectoryName(helper)!, ref si, out bootstrap)) ThrowLast();
            bootstrapJob = CreateJobObject(0, null);
            if (bootstrapJob == 0 || !SetBootstrapKill(bootstrapJob, true) || !AssignProcessToJobObject(bootstrapJob, bootstrap.Process)) ThrowLast();
            if (ResumeThread(bootstrap.Thread) == uint.MaxValue) ThrowLast();
            var replyPath = Path.Combine(root, "reply.json");
            var clock = Stopwatch.StartNew();
            while (!File.Exists(replyPath))
            {
                if (WaitForSingleObject(bootstrap.Process, 0) == 0) throw new Win32Exception(1067, "Restricted bootstrap exited before replying.");
                if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new Win32Exception(1460, "Restricted bootstrap did not become ready.");
                Thread.Sleep(20);
            }
            var reply = ReadJson<Reply>(replyPath);
            if (reply.Nonce != nonce) throw new Win32Exception(13, "Restricted launch receipt mismatch.");
            if (reply.Error != 0) throw new Win32Exception(reply.Error, reply.Message);
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
            if (!DuplicateHandle(bootstrap.Process, (nint)reply.StationHandle, GetCurrentProcess(), out station, 0, false, 2)
                || !DuplicateHandle(bootstrap.Process, (nint)reply.DesktopHandle, GetCurrentProcess(), out desktop, 0, false, 2)) ThrowLast();
            if (UserObjectText(station, 3) != "WindowStation" || UserObjectText(station, 2) == "WinSta0"
                || UserObjectText(desktop, 3) != "Desktop" || UserObjectText(desktop, 2) != "LaunchPad-" + nonce)
                throw new Win32Exception(13, "Restricted desktop handle identity mismatch.");
            // The child must hold these objects before its first user32 call;
            // otherwise the short-lived bootstrap can close the last reference.
            if (!DuplicateHandle(GetCurrentProcess(), station, childProcess, out _, 0, false, 2)
                || !DuplicateHandle(GetCurrentProcess(), desktop, childProcess, out _, 0, false, 2)) ThrowLast();
            process = Process.GetProcessById(reply.ProcessId);
            _ = process.StartTime; // Bind lifecycle identity before allowing execution.
            _ = process.Handle; // Retain the native lifecycle handle through a short-lived child exit.
            if (!park(reply.ProcessId, childProcess)) throw new Win32Exception(5, "Restricted child job assignment failed.");
            if (!SetBootstrapKill(bootstrapJob, false)) ThrowLast();
            if (ResumeThread(childThread) != 1) throw new Win32Exception(13, "Restricted child was not suspended.");
            File.WriteAllText(Path.Combine(root, "committed"), nonce);
            committed = true;
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or ArgumentException or System.Security.SecurityException)
        {
            error = exception is Win32Exception native ? native.NativeErrorCode : 13;
            try { File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { error, exception.Message })); }
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
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        Request? request = null;
        ProcessInformation child = new();
        nint token = 0, restricted = 0, station = 0, desktop = 0, descriptor = 0;
        var root = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
        var committed = false;
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
            station = CreateWindowStation(null, 0, 0x000f037f, ref attributes);
            if (station == 0 || !SetProcessWindowStation(station)) ThrowLast();
            var stationName = new StringBuilder(256);
            if (!GetUserObjectInformation(station, 2, stationName, stationName.Capacity * 2, out _)) ThrowLast();
            var desktopName = "LaunchPad-" + request.Nonce;
            desktop = CreateDesktop(desktopName, null, 0, 0, 0x000f01ff, ref attributes);
            if (desktop == 0) ThrowLast();
            var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = stationName + "\\" + desktopName };
            var command = new StringBuilder(request.CommandLine);
            if (!CreateProcessAsUser(restricted, request.Executable, command, ref attributes, ref attributes, false,
                0x08000004, 0, request.Directory, ref si, out child)) ThrowLast();
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
            WriteReply(root, new Reply(request?.Nonce ?? Path.GetFileName(root), 0, 0, 0, 0, 0, error, exception.Message));
            return error;
        }
        finally
        {
            if (!committed && child.Process != 0) TerminateProcess(child.Process, 1);
            if (child.Thread != 0) CloseHandle(child.Thread);
            if (child.Process != 0) CloseHandle(child.Process);
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
}

using System.Runtime.InteropServices;

namespace LaunchPad.Services.Fence;

public sealed class TestUserRunner
{
    public const string UserName = "BuildLaunchTest";

    public static bool LaunchAccountExists() => AccountIsPresent();

    public static bool PasswordIsStored() => PasswordFileExists();

    public static bool IsLaunchReady(bool accountExists, bool passwordStored) =>
        accountExists && passwordStored;

    public static bool LaunchAccountReady() =>
        IsLaunchReady(LaunchAccountExists(), PasswordIsStored());

    internal static void StorePassword(string password)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(password);
        var protectedBytes = Protect(plain);
        var directory = Path.GetDirectoryName(PasswordPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(PasswordPath, protectedBytes);
    }

    public static int LastStartError { get; private set; }

    public static bool TryStart(string exe, string workingDirectory, IReadOnlyList<string> arguments, out System.Diagnostics.Process? process)
    {
        process = null;
        LastStartError = 0;
        if (!AccountIsPresent() || !PasswordFileExists())
            return false;

        string password;
        try
        {
            password = ReadPassword();
        }
        catch
        {
            return false;
        }

        var command = Quote(exe);
        foreach (var argument in arguments)
            command += " " + Quote(argument);
        return StartWithCommand(exe, command, workingDirectory, password, out process);
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    public static int CommandLineLength(string exe, IReadOnlyList<string> arguments) =>
        Quote(exe).Length + arguments.Sum(argument => 1 + Quote(argument).Length);

    private readonly Func<bool> _accountExists;
    private readonly Func<bool> _passwordStored;

    public TestUserRunner(Func<bool>? accountExists = null, Func<bool>? passwordStored = null)
    {
        _accountExists = accountExists ?? AccountIsPresent;
        _passwordStored = passwordStored ?? PasswordFileExists;
    }

    public bool TryRun(string exePath, out string message)
    {
        message = "";
        if (!File.Exists(exePath) || !string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            message = "Choose a Windows program (.exe).";
            return false;
        }

        if (!_accountExists() || !_passwordStored())
        {
            message = SealText.TestAccountMissing;
            return false;
        }

        var runId = Guid.NewGuid().ToString("N");
        var runDir = Path.Combine(@"C:\Users", UserName, "runs", runId);
        try
        {
            Directory.CreateDirectory(runDir);
            RestrictedRuntimeAccess.ModifyDirectory(runDir);
            var destExe = Path.Combine(runDir, Path.GetFileName(exePath));
            File.Copy(exePath, destExe, overwrite: true);
            var sourceDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(sourceDir))
            {
                foreach (var dll in Directory.EnumerateFiles(sourceDir, "*.dll"))
                    File.Copy(dll, Path.Combine(runDir, Path.GetFileName(dll)), overwrite: true);
            }

            var password = ReadPassword();
            if (!StartAsTestUser(destExe, runDir, password, out var process))
            {
                message = SealText.TestAccountMissing;
                return false;
            }

            using (process)
            {
                try { process.WaitForExit(); }
                finally { ReleaseMachine(process.Id); }
            }
            message = "The program ran in the test account, and the copy was deleted.";
            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(runDir))
                    Directory.Delete(runDir, recursive: true);
            }
            catch
            {
                // The copy is removed on the next successful run if this delete loses a race.
            }
        }
    }

    private static bool AccountIsPresent()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = "user " + UserName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string PasswordPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaunchPad", "fence-user.bin");

    private static bool PasswordFileExists() => File.Exists(PasswordPath);

    private static string ReadPassword()
    {
        var protectedBytes = File.ReadAllBytes(PasswordPath);
        var plain = Unprotect(protectedBytes);
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    internal static byte[] Protect(byte[] plain)
    {
        var input = new DataBlob { Size = plain.Length, Data = Marshal.AllocHGlobal(plain.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(plain, 0, input.Data, plain.Length);
            if (!CryptProtectData(ref input, "LaunchPad test account", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
                LocalFree(output.Data);
        }
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        var input = new DataBlob { Size = cipher.Length, Data = Marshal.AllocHGlobal(cipher.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(cipher, 0, input.Data, cipher.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
                LocalFree(output.Data);
        }
    }

    private static bool StartAsTestUser(string exe, string workingDirectory, string password, out System.Diagnostics.Process process)
    {
        var result = StartWithCommand(exe, Quote(exe), workingDirectory, password, out var child);
        process = child!;
        return result;
    }

    private static bool StartWithCommand(string exe, string commandLine, string workingDirectory, string password, out System.Diagnostics.Process? process)
    {
        process = null;
        // CreateProcessWithLogonW returns ERROR_DIRECTORY (267) when the cwd is missing or not a directory.
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            var fallback = Path.GetDirectoryName(exe);
            if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
                workingDirectory = fallback;
            else
            {
                LastStartError = 267;
                return false;
            }
        }

        workingDirectory = Path.GetFullPath(workingDirectory);

        if (!OperatingSystem.IsWindows()) { LastStartError = 50; return false; }
        var started = RestrictedHostLaunch.Start(exe, commandLine, workingDirectory, password,
            ParkJob, out process, out var error);
        LastStartError = error;
        return started;
    }

    private const uint KillOnJobClose = 0x2000;

    public static int ExtendedJobLimitBytes => Marshal.SizeOf<JobObjectExtendedLimitInformation>();

    private static readonly object JobGate = new();

    private static readonly Dictionary<int, IntPtr> PendingJobs = new();

    private static bool ParkJob(int pid, IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
            return false;

        var created = CreateJobObject(IntPtr.Zero, null);
        if (created == IntPtr.Zero)
            return false;

        if (!AssignProcessToJobObject(created, processHandle))
        {
            CloseHandle(created);
            return false;
        }

        lock (JobGate)
            PendingJobs[pid] = created;
        return true;
    }

    public static bool HandMachineTo(int machinePid, int ownerPid)
    {
        IntPtr job;
        lock (JobGate)
        {
            if (!PendingJobs.TryGetValue(machinePid, out job) || job == IntPtr.Zero)
                return false;
            // Arm only after the independent terminal owner has the job.
            // A failed handoff retains this handle for safe cleanup.
            if (!DuplicateInto(job, ownerPid) || !SetKillOnClose(job)) return false;
            PendingJobs.Remove(machinePid);
        }
        CloseHandle(job);
        return true;
    }

    public static void ReleaseMachine(int machinePid)
    {
        IntPtr job;
        lock (JobGate)
        {
            if (!PendingJobs.Remove(machinePid, out job))
                return;
        }

        if (job != IntPtr.Zero)
            CloseHandle(job);
    }

    private static bool DuplicateInto(IntPtr job, int ownerPid)
    {
        var owner = OpenProcess(0x0040, false, ownerPid);
        if (owner == IntPtr.Zero)
            return false;

        try
        {
            return DuplicateHandle(GetCurrentProcess(), job, owner, out _, 0, false, 0x0002);
        }
        finally
        {
            CloseHandle(owner);
        }
    }

    private static bool SetKillOnClose(IntPtr job)
    {
        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = KillOnJobClose;
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var memory = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            return SetInformationJobObject(job, 9, memory, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint access,
        bool inherit,
        uint options);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int CountCharsX;
        public int CountCharsY;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved3;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithLogonW(
        string userName,
        string domain,
        string password,
        int logonFlags,
        string? applicationName,
        string? commandLine,
        int creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

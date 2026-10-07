using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record NativeGrokBinding(
    [property: JsonRequired] int Schema, [property: JsonRequired] GrokActivityBinding Activity,
    [property: JsonRequired] string NativeDirectory, [property: JsonRequired] string AppDataDirectory,
    [property: JsonRequired] string Program, [property: JsonRequired] string ProgramHash,
    [property: JsonRequired] string Token, [property: JsonRequired] int OwnerPid, [property: JsonRequired] long OwnerTicks,
    [property: JsonRequired] int ChildPid, [property: JsonRequired] long ChildTicks);

/// <summary>Optional passive native Grok hooks, owned by the console rather than the desktop.</summary>
public sealed class NativeGrokActivity : IDisposable
{
    public const string Argument = "--grok-activity-callback";
    public const string ManifestFile = "native-grok-binding.json";
    public const string CaptureFaultFile = "native-grok-capture-unavailable.json";
    public const string TokenVariable = "LAUNCHPAD_GROK_ACTIVITY_TOKEN";
    public const string DirectoryVariable = "LAUNCHPAD_GROK_ACTIVITY_DIRECTORY";
    public const string HelperVariable = "LAUNCHPAD_GROK_ACTIVITY_HELPER";
    public const string SupportedHash = "E09C0893CEE4850A569BD90E7AED956EA503B34F551637D58187CA4DFB931611";
    // Windows foreground/background/wakeup semantics are not yet proved. Native
    // receipts currently cannot enable outcome alerts merely from Linux replay.
    public const bool NativeOutcomeAlertsVerified = false;
    public const bool NativeRunLifecycleVerified = false;
    private readonly string _directory;
    private readonly string _hookPath;
    private readonly byte[] _hookBytes;
    private readonly AppPaths _paths;
    private readonly GrokActivityFeed _feed;
    private readonly GrokActivityInbox _inbox;
    private readonly GrokUpdateCapture _updates;
    private readonly GrokUpdateProjection _updateProjection;
    private NativeGrokBinding _binding;
    private bool _disposed;
    private bool _failed;

    private NativeGrokActivity(string directory, string hookPath, byte[] hookBytes, AppPaths paths, NativeGrokBinding binding)
    {
        _directory = directory; _hookPath = hookPath; _hookBytes = hookBytes; _paths = paths; _binding = binding;
        _feed = new(directory, binding.Activity); _inbox = new(directory, binding.Activity);
        _updates = new(directory, binding.Activity, GrokUpdateCapture.NativeStreamPath(paths.GrokHome, binding.Activity),
            consent: () => NativeOutcomeAlertsVerified ? new SettingsStore(paths).NotificationConsentFor(binding.Activity.HostProject) : null,
            allowLive: NativeRunLifecycleVerified);
        _updateProjection = new(directory, binding.Activity);
    }
    public string SessionId => _binding.Activity.RootSessionId;
    public bool Failed => _failed;
    public static bool IsRequest(string[] args) => args.Length == 1 && args[0] == Argument;

    public static NativeGrokActivity? TryCreate(string nativeDirectory, NativeLaunchRecord record, AppPaths paths)
    {
        string? hookPath = null; byte[]? bytes = null;
        try
        {
            if (!OperatingSystem.IsWindows() || record.Agent != AgentChoice.Grok || record.Pid <= 0 || record.StartTicks <= 0
                || Path.GetExtension(record.Program).ToLowerInvariant() != ".exe" || !File.Exists(paths.ExePath)
                || !Guid.TryParseExact(record.Generation, "N", out _)
                || !WindowsSessionWindow.MatchesProcess(record.Pid, record.StartTicks)) return null;
            using (var stream = File.OpenRead(record.Program))
                if (Convert.ToHexString(SHA256.HashData(stream)) != SupportedHash) return null;
            if (!SupportedVersion(record.Program, record.Project)) return null;
            var workspace = InspectWorkspace(record.Program, record.Project, paths.GrokHome);
            if (workspace is null) return null;
            if (!FenceFiles.TryResolveUnlinked(nativeDirectory, "grok-activity/" + record.Generation, out var directory)
                || !FenceFiles.TryResolveUnlinked(paths.GrokHome, "hooks/launchpad-" + record.Generation + ".json", out hookPath)) return null;
            Directory.CreateDirectory(directory);
            var activity = new GrokActivityBinding(record.Generation, Guid.NewGuid().ToString("D"), record.Project, record.Project, "1.0.46", workspace);
            var binding = new NativeGrokBinding(1, activity, Path.GetFullPath(nativeDirectory), Path.GetFullPath(paths.AppDataDir),
                Path.GetFullPath(record.Program), SupportedHash, Guid.NewGuid().ToString("N"), record.Pid, record.StartTicks, 0, 0);
            var manifest = Safe(directory, ManifestFile);
            // Never replace an earlier launch manifest or hook file.
            using (var output = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { JsonSerializer.Serialize(output, binding, JsonFile.Options); output.Flush(true); }
            bytes = HookBytes(binding.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
            // Grok's loader must never see partial JSON. The pending extension
            // is outside its *.json discovery, then publish without overwrite.
            var pendingHook = hookPath + "." + binding.Token + ".pending";
            using (var output = new FileStream(pendingHook, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(pendingHook, hookPath);
            return new(directory, hookPath, bytes, paths, binding);
        }
        catch
        {
            // A telemetry failure cannot substitute a CLI, alter its permissions
            // or prevent an ordinary native launch. Preserve any partial files.
            return null;
        }
    }
    private static string? InspectWorkspace(string program, string project, string grokHome)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = project,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("inspect"); start.ArgumentList.Add("--json"); start.Environment["GROK_HOME"] = grokHome;
        using var process = Process.Start(start); if (process is null) return null;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(3000)) { try { process.Kill(true); } catch { } return null; }
        var text = output.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || text.Length > 1024 * 1024) return null;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("grokVersion", out var version) || version.GetString() != "1.0.46"
            || !root.TryGetProperty("cwd", out var cwd) || cwd.GetString() != project
            || !root.TryGetProperty("projectRoot", out var workspace) || workspace.ValueKind != JsonValueKind.String) return null;
        var value = workspace.GetString();
        return value is { Length: > 0 and <= 32768 } && !value.Any(char.IsControl) ? value : null;
    }

    private static bool SupportedVersion(string program, string project)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = project,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start); if (process is null) return false;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(3000)) { try { process.Kill(true); } catch { } return false; }
        return process.ExitCode == 0 && output.GetAwaiter().GetResult().Trim() == "grok 1.0.46 (2765805b9442) [stable]";
    }

    public void Configure(ProcessStartInfo start)
    {
        start.ArgumentList.Add("--session-id"); start.ArgumentList.Add(SessionId);
        start.Environment[TokenVariable] = _binding.Token;
        start.Environment[DirectoryVariable] = _directory;
        start.Environment[HelperVariable] = _paths.ExePath;
    }
    public void Attach(Process child)
    {
        _binding = _binding with { ChildPid = child.Id, ChildTicks = child.StartTime.ToUniversalTime().Ticks };
        ReturnRecovery.SaveAtomic(Safe(_directory, ManifestFile), _binding);
    }
    public void Poll(bool connected)
    {
        if (_disposed) return;
        var context = new SessionActivityContext(_binding.NativeDirectory, _binding.Activity.Generation, _binding.Activity.HostProject, AgentChoice.Grok);
        try
        {
            // Compute a durable shadow projection while gates remain false.
            // Only the verified stream branch may publish native run events;
            // callback and stream relays never compete for the public state.
            var capture = _updates.Poll();
            _updateProjection.Ingest(capture, _updates.LiveCapturedIds, retainEvents: NativeRunLifecycleVerified);
            if (!_updates.Acknowledge(capture.Pending.Select(row => row.EventId).ToArray()))
                throw new IOException("Captured update prefix could not be acknowledged.");
            if (File.Exists(Safe(_directory, CaptureFaultFile))) throw new IOException("Native activity capture became unavailable.");
            var relay = new GrokActivityRelay(_feed, context, new NotificationOutbox(Path.Combine(_paths.AppDataDir, "notifications", "outbox")));
            var receipts = _inbox.Read().Receipts;
            foreach (var receipt in receipts)
            {
                var cursor = _feed.Read().Cursor;
                if (receipt.Sequence > cursor && !_feed.Ingest(receipt))
                    throw new InvalidDataException("Native activity receipt was refused.");
                if (!NativeRunLifecycleVerified) relay.Publish(connected);
                if (!_inbox.Acknowledge(receipt.Id)) throw new IOException("Native activity receipt could not be acknowledged.");
            }
            if (NativeRunLifecycleVerified) _updateProjection.Publish(connected, context,
                new NotificationOutbox(Path.Combine(_paths.AppDataDir, "notifications", "outbox")));
            else relay.Publish(connected);
            _failed = false;
        }
        catch
        {
            _failed = true;
            // No process-exit completion and no freshness from a timer. Publish
            // unavailable while retaining inbox/feed data for the next retry.
            try { SessionActivityStore.Publish(context, AgentActivitySnapshot.Unavailable, connected, synchronized: false, historyComplete: false); } catch { }
        }
    }

    public static int RunCallback(string[] args)
    {
        string? ownedDirectory = null;
        var stage = "binding";
        object? schema = null;
        // Never print, inject context, block approval or fail a passive hook.
        try
        {
            if (!OperatingSystem.IsWindows() || !IsRequest(args)) return 0;
            var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            var token = Environment.GetEnvironmentVariable(TokenVariable);
            if (string.IsNullOrEmpty(directory) || !Guid.TryParseExact(token, "N", out _)) return 0;
            var binding = ReadBinding(directory);
            // SessionStart can arrive before Process.Start returns. Wait only
            // for the owner to publish identity; callbacks never set it.
            var deadline = Stopwatch.StartNew();
            while (binding is { ChildPid: 0 } && deadline.ElapsedMilliseconds < 500)
            { Thread.Sleep(20); binding = ReadBinding(directory); }
            if (binding is null || binding.Token != token || !IsBoundLive(binding, directory)
                || !DescendsFrom(binding.ChildPid, binding.ChildTicks)) return 0;
            ownedDirectory = directory;
            if (File.Exists(Safe(directory, CaptureFaultFile))) return 0;
            stage = "input";
            using var input = Console.OpenStandardInput(); using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (buffer.Length <= GrokActivityAdapter.MaxInputBytes)
            {
                var count = input.Read(chunk, 0, (int)Math.Min(chunk.Length, GrokActivityAdapter.MaxInputBytes + 1 - buffer.Length));
                if (count == 0) break; buffer.Write(chunk, 0, count);
            }
            if (buffer.Length > GrokActivityAdapter.MaxInputBytes) throw new InvalidDataException("Oversized callback.");
            var raw = new UTF8Encoding(false, true).GetString(buffer.ToArray());
            stage = "schema";
            schema = Diagnose(raw, binding.Activity);
            var paths = new AppPaths(appDataDir: binding.AppDataDirectory);
            stage = "capture";
            if (new GrokActivityInbox(directory, binding.Activity).Capture(raw, DateTimeOffset.UtcNow,
                () => NativeOutcomeAlertsVerified ? new SettingsStore(paths).NotificationConsentFor(binding.Activity.HostProject) : null,
                allowRunLifecycle: NativeRunLifecycleVerified) is null)
                throw new InvalidDataException("Callback binding/schema is unavailable.");
        }
        catch (Exception failure)
        {
            if (ownedDirectory is not null)
                try { ReturnRecovery.SaveAtomic(Safe(ownedDirectory, CaptureFaultFile), new { schema = 1, unavailable = true, stage,
                    failure = failure is InvalidDataException ? "schema" : failure is IOException ? "io" : failure is UnauthorizedAccessException ? "access" : "internal",
                    nativeCode = failure.HResult & 0xffff, inputShape = schema }); } catch { }
        }
        return 0;
    }
    private static object Diagnose(string raw, GrokActivityBinding binding)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
            var fields = new Dictionary<string, string>();
            foreach (var key in new[] { "hookEventName", "hook_event_name", "sessionId", "session_id", "cwd", "workspaceRoot", "workspace_root",
                "promptId", "prompt_id", "stopHookActive", "stop_hook_active", "backgroundTasks", "background_tasks", "sessionCrons", "session_crons" })
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var field)) fields[key] = field.ValueKind.ToString();
            bool Matches(string key, string expected) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
            return new { validJson = true, bytes = Encoding.UTF8.GetByteCount(raw), fields,
                sessionMatches = Matches("sessionId", binding.RootSessionId), cwdMatches = Matches("cwd", binding.CallbackDirectory),
                workspaceMatches = Matches("workspaceRoot", binding.WorkspaceRoot ?? binding.CallbackDirectory),
                emptyPrompt = Matches("promptId", ""),
                sessionAliasMatches = !root.TryGetProperty("session_id", out _) || Matches("session_id", binding.RootSessionId),
                promptValid = root.TryGetProperty("promptId", out var prompt) && prompt.ValueKind == JsonValueKind.String && AgentActivityTracker.ValidIdentifier(prompt.GetString()),
                hasChildIdentity = new[] { "agentId", "agent_id", "subagentId", "subagent_id", "parentSessionId", "parent_session_id", "subagentType", "subagent_type" }.Any(key => root.TryGetProperty(key, out var child) && child.ValueKind != JsonValueKind.Null),
                redactionAccepted = GrokActivityInbox.TryRedact(binding, raw, out _) };
        }
        catch { return new { validJson = false, bytes = Encoding.UTF8.GetByteCount(raw) }; }
    }
    public static NativeGrokBinding? ReadBinding(string directory)
    {
        try
        {
            var file = Safe(directory, ManifestFile);
            if (!File.Exists(file) || new FileInfo(file).Length > 32768) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || doc.RootElement.EnumerateObject().Select(field => field.Name).Distinct().Count() != doc.RootElement.EnumerateObject().Count()) return null;
            var value = JsonSerializer.Deserialize<NativeGrokBinding>(doc.RootElement, JsonFile.Options);
            if (value is not { Schema: 1, Activity: not null } || value.ProgramHash != SupportedHash
                || !Guid.TryParseExact(value.Token, "N", out _) || !Guid.TryParseExact(value.Activity.Generation, "N", out _)
                || !Guid.TryParseExact(value.Activity.RootSessionId, "D", out _) || value.Activity.CliVersion != "1.0.46"
                || value.OwnerPid <= 0 || value.OwnerTicks <= 0 || value.ChildPid < 0 || value.ChildTicks < 0
                || (value.ChildPid == 0) != (value.ChildTicks == 0)
                || !Path.IsPathFullyQualified(value.NativeDirectory) || !Path.IsPathFullyQualified(value.AppDataDirectory)
                || !Path.IsPathFullyQualified(value.Program) || !Path.IsPathFullyQualified(value.Activity.HostProject)
                || value.Activity.CallbackDirectory != value.Activity.HostProject
                || Path.GetFullPath(directory) != Path.Combine(value.NativeDirectory, "grok-activity", value.Activity.Generation)) return null;
            return value;
        }
        catch { return null; }
    }
    private static bool IsBoundLive(NativeGrokBinding binding, string directory)
    {
        var record = NativeAgentTerminal.Read(binding.NativeDirectory);
        if (record is null || record.Generation != binding.Activity.Generation || record.Agent != AgentChoice.Grok
            || record.Project != binding.Activity.HostProject || record.Program != binding.Program
            || record.Pid != binding.OwnerPid || record.StartTicks != binding.OwnerTicks
            || record.AgentPid != binding.ChildPid || record.AgentStartTicks != binding.ChildTicks
            || record.State != "running" || !NativeAgentTerminal.IsLive(record)
            || !WindowsSessionWindow.MatchesProcess(binding.ChildPid, binding.ChildTicks)) return false;
        try
        {
            using var child = Process.GetProcessById(binding.ChildPid);
            return string.Equals(child.MainModule?.FileName, binding.Program, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private static bool DescendsFrom(int pid, long ticks)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var currentPid = self.Id; var latestBirth = self.StartTime.ToUniversalTime().Ticks;
            for (var depth = 0; depth < 16 && currentPid > 0; depth++)
            {
                using var current = Process.GetProcessById(currentPid);
                var birth = current.StartTime.ToUniversalTime().Ticks;
                if (birth > latestBirth) return false; // an exited parent PID was reused
                if (currentPid == pid) return birth == ticks;
                if (NtQueryInformationProcess(current.Handle, 0, out var info, Marshal.SizeOf<BasicProcessInfo>(), out _) != 0) return false;
                latestBirth = birth; currentPid = checked((int)info.ParentPid.ToInt64());
            }
        }
        catch { }
        return false;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicProcessInfo
    { public nint Reserved1, Peb, Reserved2, Reserved3, Pid, ParentPid; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int informationClass,
        out BasicProcessInfo information, int length, out int returnedLength);
    public static byte[] HookBytes(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new ArgumentException("Invalid activity launch token.");
        // Encoded source has no shell-interpolated paths. Launch data is passed
        // through the environment. Bound input and wait for only our helper.
        var script = "try { if ($env:" + TokenVariable + " -ne '" + token + "') { exit 0 }; " +
            "$s=[Diagnostics.ProcessStartInfo]::new(); $s.FileName=$env:" + HelperVariable + "; " +
            "$s.Arguments='" + Argument + "'; $s.UseShellExecute=$false; $s.CreateNoWindow=$true; " +
            "$s.RedirectStandardInput=$true; $s.RedirectStandardOutput=$true; $s.RedirectStandardError=$true; " +
            "$p=[Diagnostics.Process]::Start($s); try { $i=[Console]::OpenStandardInput(); $b=New-Object byte[] 65537; " +
            "$n=0; while ($n -lt $b.Length) { $r=$i.Read($b,$n,$b.Length-$n); if ($r -eq 0) { break }; $n+=$r }; " +
            "$p.StandardInput.BaseStream.Write($b,0,$n); $p.StandardInput.Close(); " +
            "if (!$p.WaitForExit(3000)) { $p.Kill() } } finally { $p.Dispose() } } catch {} ; exit 0";
        var command = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var hooks = new Dictionary<string, object>();
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "Stop", "StopFailure" })
            hooks[name] = new[] { new { hooks = new[] { new { type = "command", command, timeout = 5 } } } };
        return JsonSerializer.SerializeToUtf8Bytes(new { hooks });
    }
    private static string Safe(string directory, string file) => FenceFiles.TryResolveUnlinked(directory, file, out var path)
        ? path : throw new IOException("Native activity path is unavailable.");
    public void Dispose()
    {
        if (_disposed) return;
        Poll(false); _disposed = true;
        try
        {
            if (FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(_hookPath)!, Path.GetFileName(_hookPath), out var file)
                && File.Exists(file)) DeleteUnchangedHook(file, _hookBytes);
        }
        catch { /* Modified/crash leftovers stay inert without the launch token. */ }
    }
    private static void DeleteUnchangedHook(string file, byte[] bytes)
    {
        // Hold read+delete access while comparing; exclude concurrent writers
        // and renames, then mark THIS handle for deletion before releasing it.
        using var handle = CreateFile(file, 0x80000000 | 0x00010000, 1, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) return;
        using var input = new FileStream(handle, FileAccess.Read);
        if (input.Length != bytes.Length) return;
        var actual = new byte[bytes.Length]; input.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(bytes)) return;
        var disposition = new FileDisposition { Delete = true };
        SetFileInformationByHandle(handle, 4, ref disposition, Marshal.SizeOf<FileDisposition>());
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint creation, uint attributes, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass,
        ref FileDisposition disposition, int size);
}

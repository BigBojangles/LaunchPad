using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record NativeClaudeBinding([property: JsonRequired] int Schema,
    [property: JsonRequired] ClaudeActivityBinding Activity, [property: JsonRequired] string NativeDirectory,
    [property: JsonRequired] string Program, [property: JsonRequired] string Token,
    [property: JsonRequired] int OwnerPid, [property: JsonRequired] long OwnerTicks,
    [property: JsonRequired] int ChildPid, [property: JsonRequired] long ChildTicks);

/// <summary>Optional native owner/callback route. No provider credentials or user settings are stored here.</summary>
public sealed class NativeClaudeActivity : IDisposable
{
    public const string Argument = "--claude-activity-callback";
    public const string ManifestFile = "native-claude-binding.json";
    public const string CaptureFaultFile = "native-claude-capture-unavailable.json";
    public const string TokenVariable = "LAUNCHPAD_CLAUDE_ACTIVITY_TOKEN";
    public const string DirectoryVariable = "LAUNCHPAD_CLAUDE_ACTIVITY_DIRECTORY";
    public const string HelperVariable = "LAUNCHPAD_CLAUDE_ACTIVITY_HELPER";
    // Current official schemas are not native pinned-CLI delivery proof.
    public static bool NativeStatusCompatibilityVerified => false;
    private readonly string _directory, _plugin, _helper;
    private readonly ClaudeActivityFeed _feed;
    private NativeClaudeBinding _binding;
    private bool _disposed;

    private NativeClaudeActivity(string directory, string plugin, string helper, NativeClaudeBinding binding)
    { _directory = directory; _plugin = plugin; _helper = helper; _binding = binding;
        _feed = new(directory, binding.Activity, ownerEpoch: binding.Token); }

    public static bool IsRequest(string[] args) => args.Length == 1 && args[0] == Argument;
    public static NativeClaudeActivity? TryCreate(string nativeDirectory, NativeLaunchRecord record, AppPaths paths)
    {
        // Keep this check first: an unavailable adapter cannot probe or rewrite
        // the chosen CLI, create hook files, or interfere with ordinary launches.
        if (!NativeStatusCompatibilityVerified) return null;
        try
        {
            if (!OperatingSystem.IsWindows() || record.Agent != AgentChoice.Claude
                || Path.GetExtension(record.Program).ToLowerInvariant() != ".exe" || !File.Exists(paths.ExePath)
                || !WindowsSessionWindow.MatchesProcess(record.Pid, record.StartTicks)) return null;
            var versionProbe = NativeAgentTerminal.AgentStart(record);
            versionProbe.ArgumentList.Add("--version");
            versionProbe.CreateNoWindow = true; versionProbe.RedirectStandardOutput = true; versionProbe.RedirectStandardError = true;
            using (var process = Process.Start(versionProbe))
            {
                if (process is null) return null;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000)) { try { process.Kill(true); } catch { } return null; }
                if (process.ExitCode != 0 || output.GetAwaiter().GetResult().Trim() != "2.1.289 (Claude Code)") return null;
            }
            var directory = Safe(nativeDirectory, "claude-activity/" + record.Generation);
            var activity = new ClaudeActivityBinding(record.Generation, Guid.NewGuid().ToString("D"), record.Project);
            _ = new ClaudeActivityAdapter(activity); // Validate the launch contract before writes.
            var binding = new NativeClaudeBinding(1, activity, Path.GetFullPath(nativeDirectory), record.Program,
                Guid.NewGuid().ToString("N"), record.Pid, record.StartTicks, 0, 0);
            Directory.CreateDirectory(directory);
            WriteNew(Safe(directory, ManifestFile), JsonSerializer.SerializeToUtf8Bytes(binding, JsonFile.Options));
            var plugin = Safe(directory, "plugin");
            foreach (var (relative, bytes) in PluginFiles(binding.Token))
            {
                var file = Safe(plugin, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                WriteNew(Safe(plugin, relative), bytes);
            }
            return new(directory, plugin, paths.ExePath, binding);
        }
        catch { return null; } // Telemetry cannot prevent native coding. Partial owned files remain for diagnosis.
    }

    public void Configure(ProcessStartInfo start)
    {
        start.ArgumentList.Add("--session-id"); start.ArgumentList.Add(_binding.Activity.RootSessionId);
        // A hooks-only session plugin avoids replacing the user's hooks/settings.
        start.ArgumentList.Add("--plugin-dir"); start.ArgumentList.Add(_plugin);
        start.Environment[TokenVariable] = _binding.Token;
        start.Environment[DirectoryVariable] = _directory;
        start.Environment[HelperVariable] = _helper;
    }
    public void Attach(Process child)
    {
        _binding = _binding with { ChildPid = child.Id, ChildTicks = child.StartTime.ToUniversalTime().Ticks };
        ReturnRecovery.SaveAtomic(Safe(_directory, ManifestFile), _binding);
    }
    public void Poll(bool connected)
    {
        if (_disposed) return;
        var context = new SessionActivityContext(_binding.NativeDirectory, _binding.Activity.Generation,
            _binding.Activity.Project, AgentChoice.Claude);
        try
        {
            if (File.Exists(Safe(_directory, CaptureFaultFile))) throw new IOException("Claude callback capture is unavailable.");
            _feed.Publish(connected, activity => SessionActivityStore.Publish(context, activity, connected,
                synchronized: activity.State != AgentActivity.Unknown, historyComplete: false), live: NativeStatusCompatibilityVerified);
        }
        catch
        {
            try { SessionActivityStore.Publish(context, AgentActivitySnapshot.Unavailable, connected,
                synchronized: false, historyComplete: false); } catch { }
        }
    }
    public void Dispose() { if (_disposed) return; Poll(false); _disposed = true; }

    public static int RunCallback(string[] args)
    {
        // This helper is passive: no stdout/stderr context, approval or blocking return code.
        string? ownedDirectory = null;
        try
        {
            if (!OperatingSystem.IsWindows() || !IsRequest(args) || !NativeStatusCompatibilityVerified) return 0;
            var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            var token = Environment.GetEnvironmentVariable(TokenVariable);
            if (directory is null || !Guid.TryParseExact(token, "N", out _)) return 0;
            var binding = ReadBinding(directory);
            var deadline = Stopwatch.StartNew();
            while (binding is { ChildPid: 0 } && deadline.ElapsedMilliseconds < 500)
            { Thread.Sleep(20); binding = ReadBinding(directory); }
            if (binding is null || binding.Token != token || !BoundLive(binding)
                || !DescendsFrom(binding.ChildPid, binding.ChildTicks)) return 0;
            ownedDirectory = directory;
            using var input = Console.OpenStandardInput(); using var data = new MemoryStream();
            var buffer = new byte[4096];
            while (data.Length <= ClaudeActivityAdapter.MaxInputBytes)
            {
                var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, ClaudeActivityAdapter.MaxInputBytes + 1 - data.Length));
                if (count == 0) break;
                data.Write(buffer, 0, count);
            }
            if (data.Length > ClaudeActivityAdapter.MaxInputBytes) throw new InvalidDataException("Oversized Claude callback.");
            var raw = new UTF8Encoding(false, true).GetString(data.ToArray());
            new ClaudeActivityFeed(directory, binding.Activity, ownerEpoch: binding.Token).Capture(raw, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            if (ownedDirectory is not null)
                try { ReturnRecovery.SaveAtomic(Safe(ownedDirectory, CaptureFaultFile), new { schema = 1, unavailable = true,
                    category = error is InvalidDataException or DecoderFallbackException ? "schema" : "storage",
                    nativeCode = error.HResult & 0xffff }); } catch { }
        }
        return 0;
    }

    public static NativeClaudeBinding? ReadBinding(string directory)
    {
        try
        {
            using var input = new FileStream(Safe(directory, ManifestFile), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > 16384) return null;
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
            if (!Unique(document.RootElement)) return null;
            var binding = JsonSerializer.Deserialize<NativeClaudeBinding>(document.RootElement,
                new JsonSerializerOptions(JsonFile.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (binding is not { Schema: 1, Activity: not null } || !Guid.TryParseExact(binding.Token, "N", out _)
                || !Guid.TryParseExact(binding.Activity.RootSessionId, "D", out _)
                || binding.OwnerPid <= 0 || binding.OwnerTicks <= 0 || binding.ChildPid < 0 || binding.ChildTicks < 0
                || (binding.ChildPid == 0) != (binding.ChildTicks == 0)
                || !Path.IsPathFullyQualified(binding.NativeDirectory) || !Path.IsPathFullyQualified(binding.Program)
                || Path.GetExtension(binding.Program).ToLowerInvariant() != ".exe"
                || Path.GetFullPath(directory) != Path.Combine(binding.NativeDirectory, "claude-activity", binding.Activity.Generation)) return null;
            _ = new ClaudeActivityAdapter(binding.Activity);
            return binding;
        }
        catch { return null; }
    }
    private static bool BoundLive(NativeClaudeBinding binding)
    {
        var record = NativeAgentTerminal.Read(binding.NativeDirectory);
        if (record is null || record.Agent != AgentChoice.Claude || record.Generation != binding.Activity.Generation
            || record.Project != binding.Activity.Project || record.Program != binding.Program
            || record.Pid != binding.OwnerPid || record.StartTicks != binding.OwnerTicks
            || record.AgentPid != binding.ChildPid || record.AgentStartTicks != binding.ChildTicks
            || !NativeAgentTerminal.IsLive(record) || !WindowsSessionWindow.MatchesProcess(binding.ChildPid, binding.ChildTicks)) return false;
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
                if (birth > latestBirth) return false;
                if (currentPid == pid) return birth == ticks;
                if (NtQueryInformationProcess(current.Handle, 0, out var info, Marshal.SizeOf<BasicProcessInfo>(), out _) != 0) return false;
                latestBirth = birth; currentPid = checked((int)info.ParentPid.ToInt64());
            }
        }
        catch { }
        return false;
    }

    public static IReadOnlyDictionary<string, byte[]> PluginFiles(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new ArgumentException("Invalid Claude launch token.");
        var script = "try { if ($env:" + TokenVariable + " -ne '" + token + "') { exit 0 }; " +
            "$i=[Console]::OpenStandardInput(); $b=New-Object byte[] 65537; $n=0; " +
            "while ($n -lt $b.Length) { $r=$i.Read($b,$n,$b.Length-$n); if ($r -eq 0) { break }; $n+=$r }; " +
            "if ($n -gt 65536) { exit 0 }; $s=[Diagnostics.ProcessStartInfo]::new(); " +
            "$s.FileName=$env:" + HelperVariable + "; $s.Arguments='" + Argument + "'; $s.UseShellExecute=$false; $s.CreateNoWindow=$true; " +
            "$s.RedirectStandardInput=$true; $s.RedirectStandardOutput=$true; $s.RedirectStandardError=$true; " +
            "$p=[Diagnostics.Process]::Start($s); try { $p.StandardInput.BaseStream.Write($b,0,$n); $p.StandardInput.Close(); " +
            "if (!$p.WaitForExit(2000)) { $p.Kill() } } finally { $p.Dispose() } } catch {} ; exit 0";
        var command = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var hooks = new Dictionary<string, object>();
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
            "PermissionRequest", "Notification", "Stop", "StopFailure", "SessionEnd" })
            hooks[name] = new[] { new { hooks = new[] { new { type = "command", command, timeout = 4 } } } };
        return new Dictionary<string, byte[]>
        {
            [".claude-plugin/plugin.json"] = JsonSerializer.SerializeToUtf8Bytes(new { name = "launchpad-status-" + token, version = "1.0.0" }),
            ["hooks/hooks.json"] = JsonSerializer.SerializeToUtf8Bytes(new { hooks })
        };
    }
    private static void WriteNew(string path, byte[] bytes)
    { using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Write(bytes); output.Flush(true); }
    private static string Safe(string directory, string file) => FenceFiles.TryResolveUnlinked(directory, file, out var path)
        ? path : throw new IOException("Native Claude status path is unavailable.");
    private static bool Unique(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == value.EnumerateObject().Count() && value.EnumerateObject().All(field => Unique(field.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(Unique),
        _ => true
    };
    [StructLayout(LayoutKind.Sequential)] private struct BasicProcessInfo
    { public nint Reserved1, Peb, Reserved2, Reserved3, Pid, ParentPid; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int informationClass,
        out BasicProcessInfo information, int length, out int returnedLength);
}

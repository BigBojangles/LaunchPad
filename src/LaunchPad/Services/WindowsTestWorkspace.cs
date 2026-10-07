using System.Reflection;
using System.Text.Json;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record WindowsTestCopy(string Directory, string Executable, string Source);

// Preview staging only. Never writes back to the chosen program/project folder.
public static class WindowsTestWorkspace
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumFiles = 20000;

    public static WindowsTestCopy StageDemo(string runsDirectory)
    {
        var root = NewRun(runsDirectory);
        var exe = Path.Combine(root, "app", "ChecklistDemo.exe");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaunchPad.WindowsTestDemo.exe")
            ?? throw new IOException("The compiled Windows demo is missing from this build.");
        using (var output = new FileStream(exe, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { resource.CopyTo(output); output.Flush(true); }
        SaveResult(root, new { stage = "copy-ready", source = "built-in checklist demo", executable = exe });
        return new(root, exe, "built-in checklist demo");
    }

    public static WindowsTestCopy StageProgram(string runsDirectory, string projectDirectory, string executable, CancellationToken cancellation = default)
    {
        var exe = Path.GetFullPath(executable);
        var project = Path.GetFullPath(projectDirectory);
        var relativeExe = FenceFiles.Relative(project, exe);
        if (relativeExe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(exe) || !FenceFiles.TryResolveUnlinked(project, relativeExe, out _))
            throw new ArgumentException("Choose a built Windows .exe inside this project, without linked folders.");
        var source = Path.GetDirectoryName(exe)!;
        var runs = Path.GetFullPath(runsDirectory);
        if (runs.Equals(source, StringComparison.OrdinalIgnoreCase) || runs.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a program folder outside LaunchPad's managed test copies.");
        if (project == Path.GetPathRoot(project) || OperatingSystem.IsWindows() && source.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a built app in a project folder, rather than a Windows system folder or drive root.");
        var root = NewRun(runsDirectory);
        var destination = Path.Combine(root, "app");
        long bytes = 0;
        var files = 0;
        var entries = 0;
        var pending = new Stack<string>();
        pending.Push(source);
        try
        {
            while (pending.TryPop(out var directory))
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++entries > MaximumFiles * 2) throw new IOException("This preview folder contains too many entries. Choose the built app's smaller output folder.");
                    var relative = FenceFiles.Relative(source, item.FullName);
                    if (relative is null || (item.Attributes & FileAttributes.ReparsePoint) != 0
                        || !FenceFiles.TryResolveUnlinked(source, relative, out _))
                        throw new IOException("A linked or unsafe program file was found. Nothing was launched; the partial copy is retained.");
                    var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (item is DirectoryInfo) { Directory.CreateDirectory(target); pending.Push(item.FullName); continue; }
                    if (++files > MaximumFiles) throw new IOException("Preview copies are limited to 20,000 files and 2 GiB. Choose the built app's smaller output folder.");
                    using var input = new FileStream(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > MaximumBytes - bytes) throw new IOException("Preview copies are limited to 20,000 files and 2 GiB. Choose the built app's smaller output folder.");
                    bytes += input.Length;
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    input.CopyTo(output);
                    output.Flush(true);
                }
            }
            var copiedExe = Path.Combine(destination, Path.GetFileName(exe));
            SaveResult(root, new { stage = "copy-ready", source, executable = copiedExe, files, bytes });
            return new(root, copiedExe, source);
        }
        catch (Exception error)
        {
            SaveResult(root, new { stage = "copy-failed", source, error = error.Message, files, bytes });
            throw new IOException("Could not prepare the Windows test. The original is untouched. Partial copy and result: " + root, error);
        }
    }

    public static void SaveResult(string directory, object result)
    {
        ReturnRecovery.SaveAtomic(Path.Combine(directory, "result.json"), result);
        // Retain process-start identity after the current result records exit.
        File.AppendAllText(Path.Combine(directory, "events.jsonl"),
            JsonSerializer.Serialize(new { recordedUtc = DateTime.UtcNow, result }) + Environment.NewLine);
    }

    private static string NewRun(string runsDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(runsDirectory), Guid.NewGuid().ToString("N"));
        if (!FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(root)!, Path.GetFileName(root), out _)) throw new IOException("The Windows test-copy location contains a link.");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        return root;
    }
}

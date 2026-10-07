using System.Diagnostics;
using System.Globalization;

namespace LaunchPad.Services.Fence;

public static class GitBackup
{
    public static string RefName(DateTime utc) =>
        "refs/launch-pad/before-return/" + utc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);

    public static string? ReadOrigin(string project)
    {
        if (!Directory.Exists(Path.Combine(project, ".git")))
            return null;
        var url = Run(project, null, "remote", "get-url", "origin");
        if (url.Code != 0)
            return null;
        var text = url.Output.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static bool EnsureRemote(string project, string remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
            return false;
        if (!Directory.Exists(Path.Combine(project, ".git")))
        {
            if (Run(project, null, "init").Code != 0)
                return false;
        }

        var current = ReadOrigin(project);
        if (current is null)
            return Run(project, null, "remote", "add", "origin", remote).Code == 0;
        if (string.Equals(current, remote, StringComparison.Ordinal))
            return true;
        return Run(project, null, "remote", "set-url", "origin", remote).Code == 0;
    }

    public static bool TryPush(string project, string remote, out string error)
    {
        error = "";
        if (!EnsureRemote(project, remote))
        {
            error = "The backup remote could not be saved.";
            return false;
        }

        var index = Path.Combine(Path.GetTempPath(), "bl-backup-" + Guid.NewGuid().ToString("N") + ".index");
        try
        {
            var parent = Run(project, index, "rev-parse", "--verify", "HEAD");
            var parentId = parent.Code == 0 ? parent.Output.Trim() : "";
            if (parentId.Length > 0)
                Run(project, index, "read-tree", "HEAD");

            if (Run(project, index, "add", "-A").Code != 0)
            {
                error = "The project files could not be read for backup.";
                return false;
            }

            var tree = Run(project, index, "write-tree");
            if (tree.Code != 0 || string.IsNullOrWhiteSpace(tree.Output))
            {
                error = "The backup tree could not be written.";
                return false;
            }

            var treeId = tree.Output.Trim();
            GitResult commit;
            if (parentId.Length > 0)
                commit = Run(project, index, "commit-tree", treeId, "-p", parentId, "-m", "LaunchPad backup before return");
            else
                commit = Run(project, index, "commit-tree", treeId, "-m", "LaunchPad backup before return");
            if (commit.Code != 0 || string.IsNullOrWhiteSpace(commit.Output))
            {
                error = "The backup commit could not be written.";
                return false;
            }

            var reference = RefName(DateTime.UtcNow);
            var push = Run(project, null, "push", remote, commit.Output.Trim() + ":" + reference);
            if (push.Code != 0)
            {
                error = "The backup push did not complete.";
                return false;
            }

            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(index))
                    File.Delete(index);
            }
            catch
            {
                // The temporary index is outside the project.
            }
        }
    }

    private static GitResult Run(string project, string? index, params string[] args)
    {
        var git = FindGit();
        if (git is null)
            return new GitResult(1, "");

        var start = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (!string.IsNullOrEmpty(index))
            start.Environment["GIT_INDEX_FILE"] = index;
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return new GitResult(1, "");
            // Drain both pipes concurrently. Blocking ReadToEnd before the
            // deadline could keep even an optional backup in front of Save.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new GitResult(1, "");
            }
            // Descendants can retain pipe handles after git itself exits.
            if (!Task.WhenAll(output, errors).Wait(TimeSpan.FromSeconds(2)))
                return new GitResult(1, "");
            return new GitResult(process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch
        {
            return new GitResult(1, "");
        }
    }

    private static string? FindGit()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry, "git.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore a malformed PATH entry.
            }
        }

        var program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
        return File.Exists(program) ? program : null;
    }

    private readonly record struct GitResult(int Code, string Output);
}

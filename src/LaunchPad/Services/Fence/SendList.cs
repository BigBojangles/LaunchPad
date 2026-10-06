using System.Diagnostics;

namespace LaunchPad.Services.Fence;

public readonly record struct SendFile(string Full, string Relative, long Length);

public sealed class TopRow
{
    public TopRow(string name, long bytes)
    {
        Name = name;
        Bytes = bytes;
    }

    public string Name { get; }
    public long Bytes { get; }
    public bool Include { get; set; } = true;

    public string Label
    {
        get
        {
            var megabytes = Bytes / (1024 * 1024);
            var size = megabytes >= 1024
                ? (megabytes / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
                : megabytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
            return Name + "    " + size;
        }
    }
}

public static class SendList
{
    public static bool AlwaysSend(string relative) =>
        relative.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<SendFile> Collect(string root, bool sendAll)
    {
        var ignored = sendAll ? null : Ignored(root);
        var files = new List<SendFile>();
        foreach (var file in FenceFiles.Enumerate(root))
        {
            var relative = FenceFiles.Relative(root, file);
            if (relative is null)
                continue;

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch
            {
                continue;
            }

            if (ignored is not null && !AlwaysSend(relative) && ignored.Contains(relative))
                continue;

            files.Add(new SendFile(file, relative, length));
        }

        return files;
    }

    public static IReadOnlyList<TopRow> Tops(IReadOnlyList<SendFile> files)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (AlwaysSend(file.Relative))
                continue;

            var slash = file.Relative.IndexOf('/');
            var name = slash < 0 ? file.Relative : file.Relative[..slash];
            sizes.TryGetValue(name, out var soFar);
            sizes[name] = soFar + file.Length;
        }

        return sizes
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new TopRow(item.Key, item.Value))
            .ToList();
    }

    public static void AddIgnores(string root, IEnumerable<string> names)
    {
        var path = Path.Combine(root, ".gitignore");
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
                continue;

            var line = "/" + name.Trim() + "/";
            if (lines.Any(existing => string.Equals(existing.Trim(), line, StringComparison.OrdinalIgnoreCase)))
                continue;

            lines.Add(line);
        }

        File.WriteAllLines(path, lines);
    }

    public static bool InitRepo(string root)
    {
        var git = FindGit();
        if (git is null || Directory.Exists(Path.Combine(root, ".git")))
            return Directory.Exists(Path.Combine(root, ".git"));

        return Run(git, root, "init").Code == 0;
    }

    private static HashSet<string>? Ignored(string root)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")))
            return ReadTopLevelIgnores(root);

        var git = FindGit();
        if (git is null)
            return ReadTopLevelIgnores(root);

        var paths = new List<string>();
        foreach (var file in FenceFiles.Enumerate(root))
        {
            var relative = FenceFiles.Relative(root, file);
            if (relative is null || AlwaysSend(relative))
                continue;
            paths.Add(relative);
        }

        if (paths.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var done = Run(git, root, "check-ignore --stdin", string.Join("\n", paths) + "\n");
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in done.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            ignored.Add(line.Trim().Replace('\\', '/'));
        return ignored;
    }

    private static HashSet<string> ReadTopLevelIgnores(string root)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, ".gitignore");
        if (!File.Exists(path))
            return ignored;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith('/'))
                line = line[1..];
            if (line.EndsWith('/'))
                line = line[..^1];
            if (line.Length == 0 || line.Contains('/'))
                continue;
            names.Add(line);
        }

        foreach (var file in FenceFiles.Enumerate(root))
        {
            var relative = FenceFiles.Relative(root, file);
            if (relative is null || AlwaysSend(relative))
                continue;
            var slash = relative.IndexOf('/');
            var name = slash < 0 ? relative : relative[..slash];
            if (names.Contains(name))
                ignored.Add(relative);
        }

        return ignored;
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

    private static GitRun Run(string git, string root, string arguments, string? input = null)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = git,
                    Arguments = arguments,
                    WorkingDirectory = root,
                    RedirectStandardInput = input is not null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            if (!process.Start())
                return new GitRun(1, "");

            // git writes ignored paths while it reads the list. Reading after the
            // write fills the pipe and the copy never starts.
            var stdout = Task.Run(() =>
            {
                try
                {
                    return process.StandardOutput.ReadToEnd();
                }
                catch
                {
                    return "";
                }
            });
            var stderr = Task.Run(() =>
            {
                try
                {
                    process.StandardError.ReadToEnd();
                }
                catch
                {
                    // A full error pipe must not stall the ignore check.
                }
            });

            if (input is not null)
            {
                try
                {
                    process.StandardInput.Write(input);
                    process.StandardInput.Close();
                }
                catch
                {
                    // git may already have exited.
                }
            }

            if (!process.WaitForExit(120000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The copy continues without a ignore list when git hangs.
                }

                return new GitRun(1, "");
            }

            var output = stdout.Wait(5000) ? stdout.Result : "";
            stderr.Wait(1000);
            return new GitRun(process.ExitCode, output);
        }
        catch
        {
            return new GitRun(1, "");
        }
    }

    private readonly record struct GitRun(int Code, string Output);
}

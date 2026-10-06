using System.Net.Http;
using System.Text.RegularExpressions;
using LaunchPad.Models;

namespace LaunchPad.Services;

public sealed class GrokSetup : IApplicationSetup
{
    private const string PrimaryBase = "https://x.ai/cli";
    private const string FallbackBase = "https://storage.googleapis.com/grok-build-public-artifacts/cli";
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(-\S+)?$", RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly GrokLocator _locator;
    private readonly SetupLog _log;

    public GrokSetup(AppPaths paths, GrokLocator locator, SetupLog log)
    {
        _paths = paths;
        _locator = locator;
        _log = log;
    }

    public async Task<SetupStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_paths.ProjectsRoot);
            _log.Write($"Projects folder ready: {_paths.ProjectsRoot}");

            _ = _locator;
            _ = cancellationToken;
            _log.Write("Windows Grok is not installed by LaunchPad.");
            await Task.CompletedTask.ConfigureAwait(false);
            return new SetupStatus(SetupPhase.Ready, "");
        }
        catch (Exception ex)
        {
            _log.Write("Setup failed: " + ex.Message);
            return Failed();
        }
    }

    private async Task InstallGrokAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();

        var (baseUrl, version) = await ResolveVersionAsync(client, cancellationToken).ConfigureAwait(false);
        _log.Write($"Resolved Grok version {version} from {baseUrl}.");

        var tempDir = Path.Combine(Path.GetTempPath(), "LaunchPad");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"grok-windows-x86_64-{version}.exe");

        var urls = new[]
        {
            $"{baseUrl}/grok-{version}-windows-x86_64.exe",
            $"{baseUrl}/grok-{version}-windows-x86_64"
        };

        var downloaded = false;
        foreach (var url in urls)
        {
            try
            {
                _log.Write("Downloading " + url);
                await DownloadToFileAsync(client, url, tempFile, cancellationToken).ConfigureAwait(false);
                downloaded = true;
                break;
            }
            catch (Exception ex)
            {
                _log.Write("Download failed: " + ex.Message);
            }
        }

        if (!downloaded || !File.Exists(tempFile))
            throw new InvalidOperationException("Could not download Grok Build.");

        // Create missing destination folders only. Never replace existing Grok files.
        if (!File.Exists(_paths.GrokExe) || !File.Exists(_paths.AgentExe))
            Directory.CreateDirectory(_paths.GrokBin);

        CopyIfMissing(tempFile, _paths.GrokExe);
        CopyIfMissing(tempFile, _paths.AgentExe);

        try
        {
            File.Delete(tempFile);
        }
        catch
        {
            // Temp cleanup is optional.
        }
    }

    public async Task<bool> EnsureHostGrokAsync(CancellationToken cancellationToken = default)
    {
        if (_locator.IsGrokInstalled())
            return true;

        try
        {
            await InstallGrokAsync(cancellationToken).ConfigureAwait(false);
            return _locator.IsGrokInstalled();
        }
        catch (Exception ex)
        {
            _log.Write("Host Grok setup failed: " + ex.Message);
            return false;
        }
    }

    private void CopyIfMissing(string source, string destination)
    {
        if (File.Exists(destination))
        {
            _log.Write($"Leaving existing file unchanged: {destination}");
            return;
        }

        File.Copy(source, destination, overwrite: false);
        _log.Write($"Wrote {destination}");
    }

    private void EnsureUserPathHasGrokBinIfPresent()
    {
        if (!File.Exists(_paths.GrokExe))
            return;

        try
        {
            var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var entries = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Any(e => string.Equals(e, _paths.GrokBin, StringComparison.OrdinalIgnoreCase)))
                return;

            var newPath = string.IsNullOrWhiteSpace(userPath)
                ? _paths.GrokBin
                : _paths.GrokBin + ";" + userPath;
            Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.User);

            var processPath = Environment.GetEnvironmentVariable("Path") ?? "";
            if (!processPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(e => string.Equals(e, _paths.GrokBin, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable("Path", _paths.GrokBin + ";" + processPath);
            }

            _log.Write($"Added {_paths.GrokBin} to the user PATH.");
        }
        catch (Exception ex)
        {
            _log.Write("Could not update PATH: " + ex.Message);
        }
    }

    private async Task<(string BaseUrl, string Version)> ResolveVersionAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var primary = await TryReadVersionAsync(client, PrimaryBase + "/stable", cancellationToken).ConfigureAwait(false);
        if (primary is not null)
            return (PrimaryBase, primary);

        var fallback = await TryReadVersionAsync(client, FallbackBase + "/stable", cancellationToken).ConfigureAwait(false);
        if (fallback is not null)
            return (FallbackBase, fallback);

        throw new InvalidOperationException("Could not look up the Grok Build version.");
    }

    private static async Task<string?> TryReadVersionAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            return VersionPattern.IsMatch(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadToFileAsync(
        HttpClient client,
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        var temp = destination + ".partial";
        if (File.Exists(temp))
            File.Delete(temp);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(temp))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(temp, destination);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LaunchPad/1.0");
        return client;
    }

    private static SetupStatus Failed() =>
        new(SetupPhase.Failed, "Couldn’t finish setup. Check your internet and try again.");
}

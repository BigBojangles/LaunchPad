using System.Net.Http;

namespace LaunchPad.Services.Fence;

public static class LinuxGrok
{
    private const string PrimaryBase = "https://x.ai/cli";
    private const string FallbackBase = "https://storage.googleapis.com/grok-build-public-artifacts/cli";

    public static async Task<string> DownloadAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var dest = Path.Combine(directory, "grok");
        if (File.Exists(dest) && new FileInfo(dest).Length > 1_000_000)
            return dest;

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LaunchPad/1.0");
        var version = await ResolveVersionAsync(client, cancellationToken).ConfigureAwait(false);
        var names = new[]
        {
            "grok-" + version.Version + "-linux-x86_64",
            "grok-" + version.Version + "-linux-x86_64.tar.gz",
            "grok-" + version.Version + "-linux-amd64"
        };

        foreach (var name in names)
        {
            var url = version.BaseUrl.TrimEnd('/') + "/" + name;
            var partial = dest + ".partial";
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = File.Create(partial))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractGrok(partial, dest);
                    File.Delete(partial);
                }
                else
                {
                    if (File.Exists(dest))
                        File.Delete(dest);
                    File.Move(partial, dest);
                }

                return dest;
            }
            catch
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
        }

        throw new InvalidOperationException(
            "Could not download the Linux Grok build for " + version.Version + " from " + version.BaseUrl + ".");
    }

    private static void ExtractGrok(string archive, string dest)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gzip);
        System.Formats.Tar.TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            var name = Path.GetFileName(entry.Name.Replace('\\', '/'));
            if (!string.Equals(name, "grok", StringComparison.OrdinalIgnoreCase) || entry.DataStream is null)
                continue;

            using var output = File.Create(dest);
            entry.DataStream.CopyTo(output);
            return;
        }

        throw new InvalidOperationException("The Linux archive did not contain grok.");
    }

    private static async Task<(string BaseUrl, string Version)> ResolveVersionAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var primary = await TryReadVersionAsync(client, PrimaryBase + "/stable", cancellationToken).ConfigureAwait(false);
        if (primary is not null)
            return (PrimaryBase, primary);

        var fallback = await TryReadVersionAsync(client, FallbackBase + "/stable", cancellationToken).ConfigureAwait(false);
        if (fallback is not null)
            return (FallbackBase, fallback);

        throw new InvalidOperationException("Could not look up the Grok Build version.");
    }

    private static async Task<string?> TryReadVersionAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            var text = (await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false)).Trim();
            if (text.Length == 0 || text.Length > 40 || text.Any(ch => char.IsWhiteSpace(ch)))
                return null;
            return text;
        }
        catch
        {
            return null;
        }
    }
}

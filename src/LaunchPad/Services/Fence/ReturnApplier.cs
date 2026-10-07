using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LaunchPad.Services.Fence;

public sealed record ReturnApplyResult(bool Applied, string Message);

public static class ReturnApplier
{
    private sealed class Entry(string relative, string source, string target)
    {
        public string Relative { get; } = relative;
        public string Source { get; } = source;
        public string Target { get; } = target;
        public FileStream? Input;
        public FileStream? Output;
        public string NewHash = "";
        public string? OldHash;
        public FileStream? Previous;
        public bool Changed;
        public bool Created;
        public bool Unchanged;
    }

    public static async Task<ReturnApplyResult> ApplyAsync(string liveProject, ReturnRecovery recovery,
        ProjectReturnReceipt receipt, SentManifest baseline, IReturnFileScanner scanner, Func<Task<bool>> backup)
    {
        var entries = new List<Entry>();
        var newDirectories = new List<string>();
        try
        {
            if (!receipt.HasContentIdentities) return Block("The return has no verified content receipt. Received files were preserved for review.");
            var contentIdentities = receipt.Contents!.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            if (!receipt.Complete) return Block(receipt.Error ?? "The return is incomplete.");
            if (!Directory.Exists(liveProject)) return Block("The Windows project folder is missing.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in receipt.Files)
            {
                if (!seen.Add(relative) || relative.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    || !FenceFiles.TryResolveUnlinked(recovery.Payload, relative, out var source)
                    || !FenceFiles.TryResolveUnlinked(liveProject, relative, out var target))
                    return Block("The return contains an unsafe path or repository metadata.");
                var entry = new Entry(relative, source, target);
                entries.Add(entry);
                entry.Input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                entry.NewHash = Hash(entry.Input);
                var expected = contentIdentities[relative];
                if (entry.Input.Length != expected.Size || !entry.NewHash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Block("A recovery file changed or was truncated after receipt. No returned files were applied.");
                var verdict = scanner.Scan(entry.Input, relative);
                entry.Input.Position = 0;
                if (verdict != FileScanVerdict.Allowed)
                    return Block(verdict == FileScanVerdict.Unavailable
                        ? "The file scanner is unavailable. No returned files were applied."
                        : "The file scanner rejected the return. No returned files were applied.");
                if (File.Exists(target))
                {
                    // A writer or delete/rename attempt cannot share this handle.
                    entry.Output = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    if (!HasOneLink(entry.Output)) return Block("A Windows project file has multiple links or could not be verified.");
                    entry.OldHash = Hash(entry.Output);
                    entry.Unchanged = entry.OldHash == entry.NewHash;
                    if (!entry.Unchanged && !string.Equals(entry.OldHash, baseline.ContentHash(relative), StringComparison.OrdinalIgnoreCase))
                        return Block("A Windows project file changed since it was sent, or its old content could not be verified.");
                }
                else if (baseline.ContentHash(relative) is not null)
                    return Block("A Windows project file was removed since it was sent.");
            }
            if (entries.All(entry => entry.Unchanged))
            {
                recovery.Record("applied", "The return required no Windows file changes.");
                return new(true, "The return required no Windows file changes.");
            }

            // All paths, content and scans are checked before invoking the existing backup flow.
            if (!await backup().ConfigureAwait(false)) return Block("The backup did not complete. Returned files were preserved.");
            foreach (var entry in entries.Where(entry => !entry.Unchanged && entry.Output is not null))
            {
                var relative = "previous/" + entry.Relative;
                if (!FenceFiles.TryResolveUnlinked(recovery.DirectoryPath, relative, out var previousPath))
                    return Block("An original-file recovery path contains a link. Saved copies were preserved.");
                Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);
                if (!FenceFiles.TryResolveUnlinked(recovery.DirectoryPath, relative, out previousPath))
                    return Block("An original-file recovery path changed to a link. Saved copies were preserved.");
                var retained = File.Exists(previousPath);
                entry.Previous = new FileStream(previousPath, retained ? FileMode.Open : FileMode.CreateNew,
                    retained ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read);
                if (!HasOneLink(entry.Previous))
                    return Block("An original-file recovery copy has multiple links or could not be verified.");
                if (!retained)
                {
                    entry.Output!.Position = 0;
                    entry.Output.CopyTo(entry.Previous);
                    entry.Previous.Flush(flushToDisk: true);
                }
                // A previous failed attempt may already have saved this original.
                // Reuse only identical bytes, held against write/delete until rollback ends.
                if (Hash(entry.Previous) != entry.OldHash)
                    return Block("An original-file recovery copy does not match the verified Windows original. Saved copies were preserved; no returned files were applied.");
                entry.Output.Position = 0;
            }
            recovery.Record("applying", "Original Windows files are retained under previous for recovery.");
            foreach (var entry in entries.Where(entry => !entry.Unchanged))
            {
                if (!FenceFiles.TryResolveUnlinked(liveProject, entry.Relative, out var resolved) || resolved != entry.Target)
                    throw new IOException("A Windows project path changed during return.");
                if (entry.Output is null)
                {
                    EnsureParents(Path.GetDirectoryName(entry.Target)!);
                    if (!FenceFiles.TryResolveUnlinked(liveProject, entry.Relative, out _))
                        throw new IOException("A Windows project path changed to a link.");
                    entry.Output = new FileStream(entry.Target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    entry.Created = true;
                }
                entry.Changed = true;
                entry.Input!.Position = 0;
                entry.Output.Position = 0;
                entry.Input.CopyTo(entry.Output);
                entry.Output.SetLength(entry.Input.Length);
                entry.Output.Flush(flushToDisk: true);
                if (Hash(entry.Output) != entry.NewHash)
                    throw new IOException("A returned file did not match its verified content after writing.");
            }
            recovery.Record("applied", "All intended Windows file changes were verified.");
            return new(true, "All intended Windows file changes were verified.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollbackFailed = false;
            foreach (var entry in entries.Where(entry => entry.Changed).Reverse())
            {
                try
                {
                    if (entry.Created)
                    {
                        entry.Output?.Dispose(); entry.Output = null;
                        File.Delete(entry.Target);
                    }
                    else
                    {
                        var previous = entry.Previous!;
                        previous.Position = 0;
                        entry.Output!.Position = 0;
                        previous.CopyTo(entry.Output);
                        entry.Output.SetLength(previous.Length);
                        entry.Output.Flush(flushToDisk: true);
                        if (Hash(entry.Output) != entry.OldHash) rollbackFailed = true;
                    }
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException) { rollbackFailed = true; }
            }
            foreach (var directory in newDirectories.AsEnumerable().Reverse())
                try { Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return Block(rollbackFailed
                ? "Return apply failed and restoration was incomplete. Original and returned files are preserved for recovery."
                : "Return apply stopped; prior Windows file contents were retained or restored. " + error.Message);
        }
        finally
        {
            foreach (var entry in entries) { entry.Input?.Dispose(); entry.Output?.Dispose(); entry.Previous?.Dispose(); }
        }

        ReturnApplyResult Block(string message)
        {
            recovery.Record("blocked", message);
            return new(false, message + " Recovery copy: " + recovery.DirectoryPath);
        }
        void EnsureParents(string path)
        {
            if (Directory.Exists(path)) return;
            EnsureParents(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(path);
            newDirectories.Add(path);
        }
    }

    private static string Hash(Stream stream)
    {
        stream.Position = 0;
        var result = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return result;
    }

    private static bool HasOneLink(FileStream file)
    {
        // This adapter is Windows-only until the physical Mac implementation.
        return OperatingSystem.IsWindows() && GetFileInformationByHandle(file.SafeFileHandle, out var info) && info.NumberOfLinks == 1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}

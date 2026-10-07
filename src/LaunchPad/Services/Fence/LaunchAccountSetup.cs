using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LaunchPad.Services.Fence;

public static class LaunchAccountSetup
{
    public const string NotSignedIn = "Fenced start will not run as the signed-in user.";
    public const string NotBuilder = "This account is not builder.";

    public static string ScreenText => NotSignedIn + "\n" + NotBuilder;

    public static string AccountScript => CreateScript;

    public static string Explain(Action? create = null)
    {
        _ = create;
        return ScreenText;
    }

    public static bool TryCreate(out string message)
    {
        message = "";
        if (!OperatingSystem.IsWindows()) { message = "Windows setup is unavailable on this platform."; return false; }
        var exists = TestUserRunner.LaunchAccountExists();
        if (exists)
        {
            var check = TestUserRunner.CheckStoredCredential();
            if (check.Ready) return true;
            if (!check.Readable || check.Error is not (5 or 1331 or 1376)) { message = check.Message; return false; }
        }
        else if (TestUserRunner.PasswordIsStored() || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", TestUserRunner.UserName)))
        {
            message = "The account is missing but previous account data remains. Restore the original Windows account; repair will not replace its identity.";
            return false;
        }
        var password = exists ? null : NewPassword();
        var work = Path.Combine(Path.GetTempPath(), "launchpad-account-" + Guid.NewGuid().ToString("N"));
        var secret = password is null ? null : Path.Combine(work, "account.secret");
        var script = Path.Combine(work, "account.ps1");
        string? recovery = null;
        try
        {
            Directory.CreateDirectory(work);
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var (sid, rights) in new[] { (identity.User!, FileSystemRights.FullControl),
                (new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.ReadAndExecute),
                (new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(work).SetAccessControl(acl);
            if (password is not null)
            {
                var plain = Encoding.UTF8.GetBytes(password);
                try
                {
                    recovery = Path.Combine(Path.GetDirectoryName(TestUserRunner.CredentialPath)!, "fence-user-recovery-" + Guid.NewGuid().ToString("N") + ".bin");
                    TestUserRunner.StoreProtectedPassword(TestUserRunner.Protect(plain), recovery, overwrite: false);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
                File.WriteAllText(secret!, password, new UTF8Encoding(false));
            }
            File.WriteAllText(script, CreateScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var scriptLease = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var secretLease = secret is null ? null : new FileStream(secret, FileMode.Open, FileAccess.Read, FileShare.Read);
            var completed = RunElevated(script, secret);
            if (password is not null && TestUserRunner.LaunchAccountExists())
            {
                var check = TestUserRunner.CheckPassword(password);
                if (check.Ready)
                {
                    TestUserRunner.StoreProtectedPassword(File.ReadAllBytes(recovery!), TestUserRunner.CredentialPath, overwrite: false);
                    TryDelete(recovery!);
                    recovery = null;
                }
            }
            var final = TestUserRunner.CheckStoredCredential();
            if (!completed || !final.Ready)
            {
                message = (!completed ? "The elevated account helper did not finish successfully. " : "") + final.Message
                    + (recovery is null ? "" : " Encrypted recovery credential was preserved at " + recovery + ".");
                return false;
            }
            return true;
        }
        catch
        {
            message = "Windows account setup did not finish. Existing state was preserved."
                + (recovery is null ? "" : " Encrypted recovery credential was preserved at " + recovery + ".");
            return false;
        }
        finally
        {
            if (secret is not null) TryDelete(secret);
            TryDelete(script);
            try { if (Directory.Exists(work)) Directory.Delete(work); } catch { }
        }
    }

    private static bool RunElevated(string script, string? secret)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = true,
                Verb = "RunAs",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(arg);
            if (secret is not null) { start.ArgumentList.Add("-Path"); start.ArgumentList.Add(secret); }
            using var process = Process.Start(start);
            if (process is null)
                return false;
            // Do not lose ownership or delete inputs while an elevated helper
            // is still running. The repair UI awaits this on a worker thread.
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NewPassword()
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!#%+";
        var all = lower + upper + digits + symbols;
        var bytes = RandomNumberGenerator.GetBytes(32);
        var chars = new char[32];
        chars[0] = lower[bytes[0] % lower.Length];
        chars[1] = upper[bytes[1] % upper.Length];
        chars[2] = digits[bytes[2] % digits.Length];
        chars[3] = symbols[bytes[3] % symbols.Length];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[bytes[i] % all.Length];
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var swap = bytes[i % bytes.Length] % (i + 1);
            (chars[i], chars[swap]) = (chars[swap], chars[i]);
        }

        return new string(chars);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve any input that could not be removed rather than deleting its parent recursively.
        }
    }

    private const string CreateScript = """
        param([string]$Path)
        $ErrorActionPreference = 'Stop'
        $name = 'BuildLaunchTest'
        if (-not (Get-LocalUser -Name $name -ErrorAction SilentlyContinue)) {
            if (-not $Path) { throw 'A new account credential is required.' }
            $raw = [IO.File]::ReadAllText($Path)
            $sec = ConvertTo-SecureString -String $raw -AsPlainText -Force
            $raw = $null
            New-LocalUser -Name $name -Password $sec -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires | Out-Null
        }
        $sec = $null
        Enable-LocalUser -Name $name
        $account = Get-LocalUser -Name $name
        $usersGroup = (Get-LocalGroup -SID 'S-1-5-32-545').Name
        $adminsGroup = (Get-LocalGroup -SID 'S-1-5-32-544').Name
        $users = @(Get-LocalGroupMember -Group $usersGroup | Where-Object { $_.SID -eq $account.SID })
        if ($users.Count -eq 0) {
            Add-LocalGroupMember -Group $usersGroup -Member $name
        }
        $admins = @(Get-LocalGroupMember -Group $adminsGroup | Where-Object { $_.SID -eq $account.SID })
        if ($admins.Count -gt 0) {
            Remove-LocalGroupMember -Group $adminsGroup -Member $name
        }
        exit 0
        """;
}

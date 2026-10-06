using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

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
        if (TestUserRunner.LaunchAccountExists() && TestUserRunner.PasswordIsStored())
            return true;

        var password = NewPassword();
        var secret = Path.Combine(Path.GetTempPath(), "bl-launch-account-" + Guid.NewGuid().ToString("N") + ".secret");
        var script = Path.Combine(Path.GetTempPath(), "bl-launch-account-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(secret, password, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(script, CreateScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!RunElevated(script, secret))
            {
                message = SealText.TestAccountMissing;
                return false;
            }

            if (!TestUserRunner.LaunchAccountExists())
            {
                message = SealText.TestAccountMissing;
                return false;
            }

            TestUserRunner.StorePassword(password);
            return true;
        }
        catch
        {
            message = SealText.TestAccountMissing;
            return false;
        }
        finally
        {
            TryDelete(secret);
            TryDelete(script);
        }
    }

    private static bool RunElevated(string script, string secret)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Path \"" + secret + "\"",
                UseShellExecute = true,
                Verb = "RunAs",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(start);
            if (process is null)
                return false;
            if (!process.WaitForExit(120_000))
                return false;
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
            // The elevated helper also deletes the secret file.
        }
    }

    private const string CreateScript = """
        param([Parameter(Mandatory=$true)][string]$Path)
        $ErrorActionPreference = 'Stop'
        $raw = [IO.File]::ReadAllText($Path)
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        $sec = ConvertTo-SecureString -String $raw -AsPlainText -Force
        $raw = $null
        $name = 'BuildLaunchTest'
        if (-not (Get-LocalUser -Name $name -ErrorAction SilentlyContinue)) {
            New-LocalUser -Name $name -Password $sec -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires | Out-Null
        } else {
            Set-LocalUser -Name $name -Password $sec -PasswordNeverExpires -UserMayChangePassword $false
        }
        $sec = $null
        $users = @(Get-LocalGroupMember -Group 'Users' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\\BuildLaunchTest$' })
        if ($users.Count -eq 0) {
            Add-LocalGroupMember -Group 'Users' -Member $name
        }
        $admins = @(Get-LocalGroupMember -Group 'Administrators' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\\BuildLaunchTest$' })
        if ($admins.Count -gt 0) {
            Remove-LocalGroupMember -Group 'Administrators' -Member $name
        }
        exit 0
        """;
}

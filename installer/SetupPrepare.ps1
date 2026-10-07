param(
    [switch]$Elevated,
    [string]$Secret,
    [switch]$NetworkOnly,
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'

# After files are copied, prepare only LaunchPad's executable-scoped WFP rules.
# Existing account/feature setup and antivirus/firewall modes are untouched.
if ($NetworkOnly) {
    if (-not $RuntimeRoot) { throw 'A copied LaunchPad runtime is required.' }
    $runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
    $application = Join-Path $runtime 'LaunchPad.exe'
    if (-not (Test-Path -LiteralPath $application -PathType Leaf)) { throw 'LaunchPad.exe is missing.' }
    $arguments = '--prepare-host-network "' + $runtime + '"'
    $worker = Start-Process -FilePath $application -ArgumentList $arguments -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    exit $worker.ExitCode
}

function Test-Feature {
    try {
        if (-not ('WhpxProbe' -as [type])) {
            Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class WhpxProbe {
    [DllImport("WinHvPlatform.dll", ExactSpelling = true)]
    public static extern int WHvGetCapability(int code, out int value, int size, out int written);
}
'@
        }

        $value = 0
        $written = 0
        $hr = [WhpxProbe]::WHvGetCapability(0, [ref]$value, 4, [ref]$written)
        return $hr -eq 0
    }
    catch {
        return $false
    }
}

function Test-Account {
    & "$env:SystemRoot\System32\net.exe" user BuildLaunchTest *> $null
    return $LASTEXITCODE -eq 0
}

function New-LaunchPassword {
    $lower = 'abcdefghijkmnopqrstuvwxyz'.ToCharArray()
    $upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ'.ToCharArray()
    $digits = '23456789'.ToCharArray()
    $symbols = '!#%+'.ToCharArray()
    $all = $lower + $upper + $digits + $symbols
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $chars = New-Object char[] 32
    $chars[0] = $lower[$bytes[0] % $lower.Length]
    $chars[1] = $upper[$bytes[1] % $upper.Length]
    $chars[2] = $digits[$bytes[2] % $digits.Length]
    $chars[3] = $symbols[$bytes[3] % $symbols.Length]
    for ($i = 4; $i -lt 32; $i++) {
        $chars[$i] = $all[$bytes[$i] % $all.Length]
    }

    for ($i = 31; $i -gt 0; $i--) {
        $swap = $bytes[$i % 32] % ($i + 1)
        $held = $chars[$i]
        $chars[$i] = $chars[$swap]
        $chars[$swap] = $held
    }

    return -join $chars
}

function Initialize-SecretInterop {
    if (-not ('FenceSecret' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FenceSecret {
    [StructLayout(LayoutKind.Sequential)]
    public struct DataBlob {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LogonUserW(string user, string domain, string password, int type, int provider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
'@
    }
}

function Test-CandidateCredential([string]$Password) {
    Initialize-SecretInterop
    $token = [IntPtr]::Zero
    if (-not [FenceSecret]::LogonUserW('BuildLaunchTest', '.', $Password, 2, 0, [ref]$token)) { return $false }
    try {
        $identity = New-Object Security.Principal.WindowsIdentity($token)
        try { return @($identity.Groups | Where-Object { $_.Value -eq 'S-1-5-32-544' }).Count -eq 0 }
        finally { $identity.Dispose() }
    } finally { [FenceSecret]::CloseHandle($token) | Out-Null }
}

function Protect-Secret([string]$Plain, [string]$Destination) {
    Initialize-SecretInterop

    $plainBytes = [Text.Encoding]::UTF8.GetBytes($Plain)
    $input = New-Object FenceSecret+DataBlob
    $output = New-Object FenceSecret+DataBlob
    $input.Size = $plainBytes.Length
    $input.Data = [Runtime.InteropServices.Marshal]::AllocHGlobal($plainBytes.Length)
    try {
        [Runtime.InteropServices.Marshal]::Copy($plainBytes, 0, $input.Data, $plainBytes.Length)
        $ok = [FenceSecret]::CryptProtectData([ref]$input, 'LaunchPad test account', [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, 1, [ref]$output)
        if (-not $ok) {
            throw 'The account secret could not be stored.'
        }

        $protected = New-Object byte[] $output.Size
        [Runtime.InteropServices.Marshal]::Copy($output.Data, $protected, 0, $output.Size)
        $dir = Join-Path $env:APPDATA 'LaunchPad'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $temporary = $Destination + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.Write($protected, 0, $protected.Length); $stream.Flush($true) } finally { $stream.Dispose() }
            [IO.File]::Move($temporary, $Destination)
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    }
    finally {
        if ($input.Data -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::FreeHGlobal($input.Data)
        }

        if ($output.Data -ne [IntPtr]::Zero) {
            [FenceSecret]::LocalFree($output.Data) | Out-Null
        }
    }
}

if (-not $Elevated) {
    $release = Join-Path $env:LOCALAPPDATA 'Programs\LaunchPad\LaunchPad.exe'
    if (Test-Path -LiteralPath $release) {
        try {
            Start-Process -FilePath $release -ArgumentList '--release-install' -Wait -WindowStyle Hidden | Out-Null
        }
        catch {
            # Setup still copies files when the installed copy cannot stop an old machine.
        }
    }

    $featureOn = Test-Feature
    $accountOn = Test-Account
    $accountNeedsRepair = $false
    if ($accountOn) {
        try {
            $account = Get-LocalUser -Name 'BuildLaunchTest'
            $adminsGroup = (Get-LocalGroup -SID 'S-1-5-32-544').Name
            $usersGroup = (Get-LocalGroup -SID 'S-1-5-32-545').Name
            $accountNeedsRepair = -not $account.Enabled -or @(Get-LocalGroupMember -Group $adminsGroup | Where-Object { $_.SID -eq $account.SID }).Count -gt 0 -or @(Get-LocalGroupMember -Group $usersGroup | Where-Object { $_.SID -eq $account.SID }).Count -eq 0
        } catch { $accountNeedsRepair = $true }
    }
    if ($featureOn -and $accountOn -and -not $accountNeedsRepair) {
        exit 0
    }

    $createdPassword = $null
    $secretPath = $null
    $recoveryPath = $null
    $workPath = $null
    if (-not $accountOn) {
        try {
        $existingSecret = Join-Path $env:APPDATA 'LaunchPad\fence-user.bin'
        $oldProfile = Join-Path (Split-Path -Parent $env:USERPROFILE) 'BuildLaunchTest'
        if ((Test-Path -LiteralPath $existingSecret) -or (Test-Path -LiteralPath $oldProfile)) {
            throw 'The account is missing but previous account data remains. Restore the original Windows account; setup will not replace its identity.'
        }
        $createdPassword = New-LaunchPassword
        $recoveryPath = Join-Path $env:APPDATA ('LaunchPad\fence-user-recovery-' + [guid]::NewGuid().ToString('N') + '.bin')
        Protect-Secret $createdPassword $recoveryPath
        $workPath = Join-Path ([IO.Path]::GetTempPath()) ('launchpad-account-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($workPath) | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        foreach ($entry in @(@($currentSid, 'FullControl'), @('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'ReadAndExecute'))) {
            $sid = New-Object Security.Principal.SecurityIdentifier($entry[0].ToString())
            $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, [Security.AccessControl.FileSystemRights]$entry[1], [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $workPath -AclObject $acl
        $secretPath = Join-Path $workPath 'account.secret'
        [IO.File]::WriteAllText($secretPath, $createdPassword, (New-Object System.Text.UTF8Encoding $false))
        }
        catch {
            if ($secretPath) { Remove-Item -LiteralPath $secretPath -Force -ErrorAction SilentlyContinue }
            if ($workPath) { Remove-Item -LiteralPath $workPath -ErrorAction SilentlyContinue }
            throw
        }
    }

    $argList = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Elevated'
    if ($secretPath) {
        $argList += ' -Secret "' + $secretPath + '"'
    }

    $scriptLease = $null
    $secretLease = $null
    try {
        $scriptLease = New-Object IO.FileStream($PSCommandPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($secretPath) { $secretLease = New-Object IO.FileStream($secretPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
        $elevated = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $argList -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    }
    catch {
        exit 1
    }
    finally {
        if ($secretLease) { $secretLease.Dispose() }
        if ($scriptLease) { $scriptLease.Dispose() }
        if ($secretPath) { Remove-Item -LiteralPath $secretPath -Force -ErrorAction SilentlyContinue }
        if ($workPath) { Remove-Item -LiteralPath $workPath -ErrorAction SilentlyContinue }
    }

    if ($elevated.ExitCode -ne 0) {
        exit $elevated.ExitCode
    }

    if ($createdPassword) {
        # Current-user DPAPI was prepared before account creation. Publication
        # never overwrites an existing host credential; failed setup retains
        # the encrypted recovery candidate without resetting the account.
        if (-not (Test-CandidateCredential $createdPassword)) {
            throw ('Windows rejected the new account credential. The account password was not reset; the encrypted recovery credential remains at ' + $recoveryPath)
        }
        [IO.File]::Move($recoveryPath, (Join-Path $env:APPDATA 'LaunchPad\fence-user.bin'))
    }

    $createdPassword = $null
    exit 0
}

if (-not (Test-Feature)) {
    $dism = Start-Process -FilePath "$env:SystemRoot\System32\dism.exe" -ArgumentList @('/online', '/Enable-Feature', '/FeatureName:HypervisorPlatform', '/NoRestart') -Wait -PassThru -WindowStyle Hidden
    if ($dism.ExitCode -ne 0 -and $dism.ExitCode -ne 3010) {
        exit $dism.ExitCode
    }
}

if ($Secret -and -not (Test-Account)) {
    $raw = [IO.File]::ReadAllText($Secret)
    $secure = ConvertTo-SecureString -String $raw -AsPlainText -Force
    $raw = $null
    Import-Module Microsoft.PowerShell.LocalAccounts
    New-LocalUser -Name 'BuildLaunchTest' -Password $secure -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires | Out-Null
    $secure = $null
}

if (Test-Account) {
    Enable-LocalUser -Name 'BuildLaunchTest'
    $account = Get-LocalUser -Name 'BuildLaunchTest'
    $usersGroup = (Get-LocalGroup -SID 'S-1-5-32-545').Name
    $adminsGroup = (Get-LocalGroup -SID 'S-1-5-32-544').Name
    if (@(Get-LocalGroupMember -Group $usersGroup | Where-Object { $_.SID -eq $account.SID }).Count -eq 0) {
        Add-LocalGroupMember -Group $usersGroup -Member 'BuildLaunchTest'
    }
    if (@(Get-LocalGroupMember -Group $adminsGroup | Where-Object { $_.SID -eq $account.SID }).Count -gt 0) {
        Remove-LocalGroupMember -Group $adminsGroup -Member 'BuildLaunchTest'
    }
}

exit 0

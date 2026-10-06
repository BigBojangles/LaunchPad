# Owned-fixture probe run through the production launch-account runner.
# Opens requested file/process rights only; never reads process memory, signals
# another process, changes protection, or opens user files other than the canary.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Configuration, [switch]$Child)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
$reportRoot = Split-Path -Parent $Configuration
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$result = [ordered]@{
    schema = 1
    capturedUtc = [DateTime]::UtcNow.ToString('o')
    identity = $identity.Name
    sid = $identity.User.Value
    groups = @($identity.Groups | ForEach-Object { $_.Value })
    isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    canary = @()
    readOnlyCanary = @()
    processRights = @()
    finished = $false
}
try {
    if ($identity.Name.Split('\')[-1] -ne 'BuildLaunchTest' -or $result.isAdministrator) {
        throw 'Actual standard launch account required.'
    }
    $canary = [IO.Path]::GetFullPath($config.canary)
    $workspace = [IO.Path]::GetFullPath($config.workspace).TrimEnd('\') + '\'
    if (!$canary.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Canary outside the owned workspace.'
    }
    foreach ($access in @([IO.FileAccess]::Read, [IO.FileAccess]::Write)) {
        $entry = [ordered]@{ path = $canary; access = $access.ToString(); opened = $false; hresult = $null; error = $null }
        try {
            $file = [IO.File]::Open($canary, [IO.FileMode]::Open, $access, [IO.FileShare]::ReadWrite)
            $file.Dispose() # No content read, write, creation or truncation.
            $entry.opened = $true
        } catch {
            $errorObject = $_.Exception
            while ($errorObject.InnerException) { $errorObject = $errorObject.InnerException }
            $entry.hresult = $errorObject.HResult
            $entry.error = $errorObject.GetType().FullName
        }
        $result.canary += [pscustomobject]$entry
    }
    if ($config.readOnlyCanary) {
        $readOnly = [IO.Path]::GetFullPath($config.readOnlyCanary)
        if (!$readOnly.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Read-only control outside owned workspace' }
        foreach ($access in @([IO.FileAccess]::Read, [IO.FileAccess]::Write)) {
            $entry = [ordered]@{path=$readOnly;access=$access.ToString();opened=$false;hresult=$null}
            try {
                $file=[IO.File]::Open($readOnly,[IO.FileMode]::Open,$access,[IO.FileShare]::ReadWrite)
                $file.Dispose() # No bytes read, written or truncated.
                $entry.opened=$true
            } catch {
                $errorObject=$_.Exception
                while($errorObject.InnerException) {$errorObject=$errorObject.InnerException}
                $entry.hresult=$errorObject.HResult
            }
            $result.readOnlyCanary += [pscustomobject]$entry
        }
    }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OwnedBoundaryProcess {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr OpenProcess(uint rights, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    public static extern bool GetUserObjectInformation(IntPtr handle, int information, System.Text.StringBuilder text, int bytes, out int required);
    public static string[] DesktopNames() {
        var names=new System.Collections.Generic.List<string>();
        foreach(var handle in new[]{GetProcessWindowStation(),GetThreadDesktop(GetCurrentThreadId())}) {
            var text=new System.Text.StringBuilder(512);
            int bytes;
            if(!GetUserObjectInformation(handle,2,text,text.Capacity*2,out bytes))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            names.Add(text.ToString());
        }
        return names.ToArray();
    }
    [DllImport("advapi32.dll", SetLastError=true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError=true)]
    public static extern bool GetTokenInformation(IntPtr token, int information, IntPtr buffer, int bytes, out int required);
    public static string[] LogonSids() { return GroupSids(2, true); }
    public static string[] RestrictedSids() { return GroupSids(11, false); }
    private static string[] GroupSids(int information, bool logonsOnly) {
        IntPtr token;
        if(!OpenProcessToken(GetCurrentProcess(), 8, out token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buffer=IntPtr.Zero;
        try {
            int bytes;
            GetTokenInformation(token, information, IntPtr.Zero, 0, out bytes);
            if(bytes<=0 || bytes>1048576) throw new InvalidOperationException("Invalid owned token group buffer");
            buffer=Marshal.AllocHGlobal(bytes);
            if(!GetTokenInformation(token, information, buffer, bytes, out bytes))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var count=Marshal.ReadInt32(buffer);
            var offset=IntPtr.Size==8 ? 8 : 4;
            var stride=IntPtr.Size==8 ? 16 : 8;
            if(count<0 || count>(bytes-offset)/stride) throw new InvalidOperationException("Invalid owned token group count");
            var values=new System.Collections.Generic.List<string>();
            for(int index=0; index<count; index++) {
                var entry=IntPtr.Add(buffer, offset+index*stride);
                var flags=unchecked((uint)Marshal.ReadInt32(entry, IntPtr.Size));
                if(!logonsOnly || (flags&0xc0000000u)==0xc0000000u) {
                    var sid=new System.Security.Principal.SecurityIdentifier(Marshal.ReadIntPtr(entry)).Value;
                    values.Add(logonsOnly ? sid+":"+flags.ToString("x8") : sid);
                }
            }
            return values.ToArray();
        } finally {
            if(buffer!=IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }
}
'@
    $result.logonSids = @([OwnedBoundaryProcess]::LogonSids())
    $result.restrictingSids = @([OwnedBoundaryProcess]::RestrictedSids())
    $result.temporaryDirectory = [IO.Path]::GetTempPath()
    $result.stationAndDesktop = @([OwnedBoundaryProcess]::DesktopNames())
    foreach ($owner in $config.owners) {
    foreach ($request in @(
        @{ name = 'QUERY_LIMITED_INFORMATION'; mask = 0x1000 },
        @{ name = 'VM_READ'; mask = 0x10 },
        @{ name = 'VM_WRITE'; mask = 0x20 },
        @{ name = 'VM_OPERATION'; mask = 0x8 },
        @{ name = 'CREATE_THREAD'; mask = 0x2 },
        @{ name = 'DUP_HANDLE'; mask = 0x40 },
        @{ name = 'WRITE_DAC'; mask = 0x40000 },
        @{ name = 'TERMINATE'; mask = 0x1 }
    )) {
        $handle = [OwnedBoundaryProcess]::OpenProcess($request.mask, $false, $owner.pid)
        $lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $opened = $handle -ne [IntPtr]::Zero
        if ($opened) { [void][OwnedBoundaryProcess]::CloseHandle($handle) }
        $result.processRights += [pscustomobject]@{
            owner = $owner.name; pid = $owner.pid; name = $request.name; mask = $request.mask; opened = $opened
            win32Error = $(if ($opened) { 0 } else { $lastError })
        }
    }
    }
    [IO.File]::WriteAllText((Join-Path $reportRoot $(if ($Child) { 'allowed-child-write.txt' } else { 'allowed-write.txt' })), 'owned launch-account write')
    if (!$Child) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Configuration "' + $Configuration + '" -Child'
        $start.WorkingDirectory = $reportRoot
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $childProcess = [Diagnostics.Process]::Start($start)
        if (!$childProcess.WaitForExit(20000)) { $childProcess.Kill(); throw 'Owned descendant probe timed out.' }
        $result.childExit = $childProcess.ExitCode
        $childProcess.Dispose()
    }
    $result.finished = $true
} catch {
    $result.error = $_.Exception.Message
} finally {
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot $(if ($Child) { 'host-boundary-child-private.json' } else { 'host-boundary-private.json' })) -Encoding UTF8
}
if (!$result.finished) { exit 1 }

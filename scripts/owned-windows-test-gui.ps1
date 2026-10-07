param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OwnedGuiDesktop {
    [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int kind, StringBuilder name, int size, out int required);
    public static string Name(IntPtr handle) { var name=new StringBuilder(512); int size; if(!GetUserObjectInformation(handle,2,name,1024,out size)) throw new Exception("Owned GUI desktop identity unavailable"); return name.ToString(); }
}
'@
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $childStart = New-Object Diagnostics.ProcessStartInfo
    $childStart.FileName = Join-Path $PSHOME 'powershell.exe'
    $childStart.Arguments = '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 120"'
    $childStart.UseShellExecute = $false
    $childStart.CreateNoWindow = $true
    $child = [Diagnostics.Process]::Start($childStart)
    $form = New-Object Windows.Forms.Form
    $form.Text = 'LaunchPad owned GUI fixture'
    $form.Width = 520
    $form.Height = 200
    $form.Left = 100
    $form.Top = 220
    $form.StartPosition = 'Manual'
    $textBox = New-Object Windows.Forms.TextBox
    $textBox.Left = 20
    $textBox.Top = 30
    $textBox.Width = 440
    $form.Controls.Add($textBox)
    $textBox.Add_TextChanged({
        [IO.File]::WriteAllText((Join-Path $Root 'input.json'), (@{ text = $textBox.Text; pid = $PID } | ConvertTo-Json -Compress))
    })
    $form.Add_Shown({
        $form.Activate()
        $textBox.Focus() | Out-Null
        $data = @{ pid = $PID; childPid = $child.Id; identity = $identity.Name; admin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); station = [OwnedGuiDesktop]::Name([OwnedGuiDesktop]::GetProcessWindowStation()); desktop = [OwnedGuiDesktop]::Name([OwnedGuiDesktop]::GetThreadDesktop([OwnedGuiDesktop]::GetCurrentThreadId())); window = $form.Handle.ToInt64() }
        [IO.File]::WriteAllText((Join-Path $Root 'gui-ready.json'), ($data | ConvertTo-Json -Compress))
    })
    [Windows.Forms.Application]::Run($form)
} catch {
    [IO.File]::WriteAllText((Join-Path $Root 'gui-error.txt'), ($_ | Out-String))
    exit 1
}

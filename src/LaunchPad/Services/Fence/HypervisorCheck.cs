using System.Diagnostics;
using System.Runtime.InteropServices;
using LaunchPad.Models;

namespace LaunchPad.Services.Fence;

public sealed record HypervisorState(bool FeatureEnabled, bool HypervisorPresent, bool FirmwareEnabled, string Vendor);

public sealed record HypervisorDecision(
    bool Show,
    bool RequestRestart,
    bool RequestElevation,
    bool SetResume,
    bool ClearResume,
    string Message);

public static class HypervisorCheck
{
    public static HypervisorState Query()
    {
        var feature = WhpxFeatureEnabled();
        var (present, firmware, vendor) = ReadMachine();
        return new HypervisorState(feature, present, firmware, vendor);
    }

    public static HypervisorDecision Decide(HypervisorState state, bool resumePending)
    {
        if (state.FeatureEnabled && state.HypervisorPresent)
        {
            return new HypervisorDecision(
                Show: false,
                RequestRestart: false,
                RequestElevation: false,
                SetResume: false,
                ClearResume: resumePending,
                Message: "");
        }

        if (!state.FeatureEnabled)
        {
            return new HypervisorDecision(
                Show: true,
                RequestRestart: true,
                RequestElevation: true,
                SetResume: true,
                ClearResume: false,
                Message: "Windows Hypervisor Platform is off. One administrator click turns it on. Windows restarts, then LaunchPad continues.");
        }

        if (!state.FirmwareEnabled)
        {
            var amd = state.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
            var intel = state.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
            var message = amd
                ? "Turn on SVM Mode in the firmware, then start LaunchPad again."
                : intel
                    ? "Turn on Intel Virtualization Technology in the firmware, then start LaunchPad again."
                    : "Turn on the firmware virtualization setting, then start LaunchPad again.";
            return new HypervisorDecision(
                Show: true,
                RequestRestart: false,
                RequestElevation: false,
                SetResume: false,
                ClearResume: false,
                Message: message);
        }

        return new HypervisorDecision(
            Show: true,
            RequestRestart: true,
            RequestElevation: false,
            SetResume: resumePending,
            ClearResume: false,
            Message: "Restart Windows so the hypervisor can start. LaunchPad continues after that restart.");
    }

    public static HypervisorDecision Apply(AppSettings settings, HypervisorState state, Action save)
    {
        var decision = Decide(state, settings.HypervisorResumePending);
        var changed = false;
        if (decision.ClearResume && settings.HypervisorResumePending)
        {
            settings.HypervisorResumePending = false;
            changed = true;
        }

        if (decision.SetResume && !settings.HypervisorResumePending)
        {
            settings.HypervisorResumePending = true;
            changed = true;
        }

        if (changed)
            save();
        return decision;
    }

    public static int AcceptElevation(HypervisorDecision decision, Action enable)
    {
        if (!decision.RequestElevation)
            return 0;
        enable();
        return 1;
    }

    public static bool TryEnableFeature()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = "/online /Enable-Feature /FeatureName:HypervisorPlatform /NoRestart",
                UseShellExecute = true,
                Verb = "RunAs",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(180_000))
                return false;
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool WhpxFeatureEnabled()
    {
        try
        {
            var hr = WHvGetCapability(0, out _, 4, out _);
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (bool Present, bool Firmware, string Vendor) ReadMachine()
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"$cs = Get-CimInstance Win32_ComputerSystem; $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1; Write-Output ('HYPERVISOR=' + $cs.HypervisorPresent); Write-Output ('FIRMWARE=' + $cpu.VirtualizationFirmwareEnabled); Write-Output ('VENDOR=' + $cpu.Manufacturer)\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not read the hypervisor state.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(20_000);
        var present = LineIsTrue(output, "HYPERVISOR=");
        var firmware = LineIsTrue(output, "FIRMWARE=");
        var vendor = LineValue(output, "VENDOR=");
        return (present, firmware, vendor);
    }

    private static bool LineIsTrue(string output, string prefix)
    {
        var value = LineValue(output, prefix);
        return value.Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    private static string LineValue(string output, string prefix)
    {
        foreach (var raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return "";
    }

    [DllImport("WinHvPlatform.dll", ExactSpelling = true)]
    private static extern int WHvGetCapability(int code, out int value, int size, out int written);
}

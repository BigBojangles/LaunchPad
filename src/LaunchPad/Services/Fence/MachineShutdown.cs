using System.Diagnostics;

namespace LaunchPad.Services.Fence;

public static class MachineShutdown
{
    /// <summary>True only when a live VM exits after a guest shutdown request.</summary>
    public static bool WaitForGuestExit(Process machine, int qmpPort, TimeSpan? timeout = null)
    {
        try
        {
            if (machine.HasExited || !ConsoleSizeLink.RequestPowerDown(qmpPort))
                return false;
            var wait = timeout ?? TimeSpan.FromSeconds(30);
            return machine.WaitForExit((int)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

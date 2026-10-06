using LaunchPad.Models;

namespace LaunchPad.Services;

public interface IApplicationSetup
{
    Task<SetupStatus> EnsureReadyAsync(CancellationToken cancellationToken = default);
}

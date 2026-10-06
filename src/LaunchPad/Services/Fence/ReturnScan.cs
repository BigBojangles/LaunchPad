namespace LaunchPad.Services.Fence;

public sealed record ScanReport(bool IsStub, string PlainStatement);

public interface IReturnScan
{
    ScanReport Scan(string asideDirectory);
}

public sealed class StubReturnScan : IReturnScan
{
    public ScanReport Scan(string asideDirectory)
    {
        _ = asideDirectory;
        return new ScanReport(true, SealText.Stub);
    }
}

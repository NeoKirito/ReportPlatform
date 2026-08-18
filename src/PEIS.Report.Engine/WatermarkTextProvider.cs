namespace PEIS.Report.Engine;

/// <summary>
/// Resolves the organization-owned text used for report watermarks. Implementations must not expose renderer-specific
/// types so report contracts, controllers, and print clients remain independent of FastReport.
/// </summary>
public interface IWatermarkTextProvider
{
    Task<string?> GetWatermarkTextAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Explicit safe default for environments where the maintenance database has not been configured. Rendering continues
/// without a watermark instead of substituting unverified or caller-supplied text.
/// </summary>
public sealed class DisabledWatermarkTextProvider : IWatermarkTextProvider
{
    public Task<string?> GetWatermarkTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}

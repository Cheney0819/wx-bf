namespace Footprint;

public interface IFootprintBootstrapRuntime
{
    Task EnsureBackgroundAsync(CancellationToken cancellationToken);
}

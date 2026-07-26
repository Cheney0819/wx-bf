namespace DesktopPet.DataSync.Identity;

public sealed record ClientIdentityDocument(
    int SchemaVersion,
    string SessionId,
    string Source,
    DateTimeOffset CreatedAtUtc);

public interface IClientIdentityProvider
{
    Task<ClientIdentityDocument> GetAsync(CancellationToken cancellationToken);
}

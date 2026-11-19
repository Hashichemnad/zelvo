using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Zelvo.Secrets;

namespace Zelvo.Api.Secrets;

/// <summary>
/// Readiness: the key-encryption key is configured and works (a random value is wrapped and
/// unwrapped). For Vault this also shows that Vault is reachable and the token is accepted.
/// </summary>
internal sealed class KeyEncryptionKeyHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = services.GetRequiredService<KeyRing>().Current;
            var probe = RandomNumberGenerator.GetBytes(32);
            var wrapped = await key.WrapAsync(probe, cancellationToken);
            var unwrapped = await key.UnwrapAsync(key.Id, wrapped, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(probe, unwrapped)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The key-encryption key returned a different value.");
        }
        catch (KeyEncryptionKeyException ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}

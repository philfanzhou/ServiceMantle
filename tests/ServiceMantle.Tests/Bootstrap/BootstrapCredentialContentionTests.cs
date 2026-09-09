using ServiceMantle.Bootstrap;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the one-time boundary under contention: many callers presenting valid and wrong
/// candidates at once, of which exactly one may consume the credential.
/// </summary>
/// <remarks>
/// This class is deliberately separate from the deterministic sharing tests. Windows classifies a
/// claim that lost this race in a way this store does not yet report correctly, which is tracked in
/// #355, so the Windows job runs the sharing evidence and not this contention.
/// </remarks>
public sealed class BootstrapCredentialContentionTests
{
    private static readonly ServiceId Service = ServiceId.Parse("credential-worker");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sixteen_contenders_produce_exactly_one_success_in_every_round()
    {
        const int Rounds = 20;
        const int Contenders = 16;

        for (var round = 0; round < Rounds; round++)
        {
            using var directory = TemporaryDirectory.Create();
            var provisioned = await Create(directory).ProvisionAsync(
                BootstrapCredentialLifetime.Default,
                Token);
            var candidate = provisioned.Credential!.Reveal();
            var candidates = Enumerable.Range(0, Contenders)
                .Select(index => index % 2 == 0 ? candidate : BootstrapCredential.Generate().Reveal())
                .ToArray();
            using var start = new Barrier(candidates.Length);

            var results = await Task.WhenAll(candidates.Select(value => Task.Run(async () =>
            {
                var store = Create(directory);
                start.SignalAndWait(Token);
                return await store.ConsumeAsync(value, Token);
            }, Token)));

            Assert.Single(results, result => result.IsConsumed);
            Assert.All(
                results.Where(result => !result.IsConsumed),
                result => Assert.Equal(
                    WellKnownBootstrapCredentialErrorCodes.Invalid,
                    result.ErrorCode));
        }
    }

    private static BootstrapCredentialFileStore Create(TemporaryDirectory directory) => new(
        Service,
        Path.Combine(directory.Path, "credential.json"),
        Path.Combine(directory.Path, "bootstrap.json"));
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Installation.PostgreSql;

/// <summary>
/// The operator command that rotates the outstanding one-time Setup Code:
/// <c>dotnet ServiceMantle.ReferenceService.dll --rotate-setup-code &lt;gate arguments&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The command runs before any web host is built: it reads the same PostgreSQL gate inputs as a
/// normal start, opens one short-lived context on the target connection string, and rotates - or,
/// when nothing was ever issued, creates - the code. It never prepares a database, never runs a
/// migration, and never starts listening.
/// </para>
/// <para>
/// Exit codes are fixed: <c>0</c> after the new code was printed in the same banner format the
/// startup issuer uses, <c>1</c> for a completed installation, a missing installation row, any
/// other rejection, or any failure (each answered with one fixed stderr line that names no
/// provider text and never the plaintext), and <c>2</c> when the PostgreSQL gate is not enabled,
/// because then there is no installation to rotate at all.
/// </para>
/// </remarks>
public static class ReferenceSetupCodeRotation
{
    /// <summary>Runs the rotation command and returns its exit code.</summary>
    /// <param name="configuration">The host configuration carrying the gate inputs.</param>
    /// <param name="output">The console seam; defaults to the real console when null.</param>
    public static async Task<int> RunAsync(
        IConfiguration configuration,
        ReferenceSetupCodeOutput? output = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var target = output ?? new ReferenceSetupCodeOutput(Console.Out, Console.Error);

        var options = ReferencePostgreSqlStartupOptions.Read(configuration);
        if (options is null)
        {
            await target.Error.WriteLineAsync(
                "the setup code rotation requires '" +
                ReferencePostgreSqlStartupOptions.EnabledKey + "' to be true.").ConfigureAwait(false);
            return 2;
        }

        SetupCodeIssueResult issued;
        try
        {
            var context = new ReferencePostgreSqlDbContext(
                new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
                    .UseNpgsql(options.TargetConnectionString)
                    .Options);
            await using (context.ConfigureAwait(false))
            {
                var store = new EfCoreServiceSetupCodeStore<ReferencePostgreSqlDbContext>(context);
                issued = await store.RotateAsync(ReferenceApplication.Service, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!issued.IsIssued && issued.ErrorCode == WellKnownSetupCodeErrorCodes.NotCreated)
                {
                    // A pending installation that never got a code (for example an issuance that
                    // failed at first startup): create one instead.
                    issued = await store.CreateAsync(ReferenceApplication.Service, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            await target.Error.WriteLineAsync(ReferenceSetupCodeIssuer.IssuanceFailedHint)
                .ConfigureAwait(false);
            return 1;
        }

        if (issued.IsIssued)
        {
            await target.Out.WriteLineAsync("one-time setup code:").ConfigureAwait(false);
            await target.Out.WriteLineAsync(issued.SetupCode!.Reveal()).ConfigureAwait(false);
            await target.Out.WriteLineAsync(
                "expires at " + issued.ExpiresAtUtc!.Value.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await target.Out.WriteLineAsync(ReferenceSetupCodeIssuer.RotationHint).ConfigureAwait(false);
            return 0;
        }

        // installation.completed, installation.not_found, and every other rejection share one
        // fixed stderr line and one exit code.
        await target.Error.WriteLineAsync(
            "the setup code could not be rotated; " + ReferenceSetupCodeIssuer.RotationHint)
            .ConfigureAwait(false);
        return 1;
    }
}

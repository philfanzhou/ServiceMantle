using Microsoft.Extensions.Configuration;
using ServiceMantle.ReferenceService;
using ServiceMantle.ReferenceService.Installation.PostgreSql;

// The operator command runs before any web host exists: it rotates (or first creates) the
// outstanding one-time Setup Code and exits with a fixed code.
if (args.Contains("--rotate-setup-code"))
{
    var configuration = new ConfigurationBuilder()
        .AddCommandLine(args)
        .AddEnvironmentVariables()
        .Build();
    return await ReferenceSetupCodeRotation.RunAsync(configuration);
}

var builder = ReferenceApplication.CreateBuilder(args);
await using var application = ReferenceApplication.Build(builder);
await application.RunAsync();
return 0;

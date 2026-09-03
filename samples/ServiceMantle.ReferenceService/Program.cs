using ServiceMantle.ReferenceService;

var builder = ReferenceApplication.CreateBuilder(args);
await using var application = ReferenceApplication.Build(builder);
await application.RunAsync();

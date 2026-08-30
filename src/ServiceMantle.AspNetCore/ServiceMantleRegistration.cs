using ServiceMantle;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Records the host identity fixed by <c>AddServiceMantle</c> and marks the container as having
/// been configured by it.
/// </summary>
/// <remarks>
/// This marker is assembly-internal on purpose. It is the one registration a consumer cannot make
/// on its own, so resolving it is the only reliable proof that <c>AddServiceMantle</c> ran. The
/// public services that call registers - <see cref="ServiceId"/>, <see cref="InstanceId"/>,
/// <c>BootstrapFileStore</c>, <c>ServiceLogContext</c> - are not proof, because a consumer can
/// register any of them directly.
/// </remarks>
internal sealed record ServiceMantleRegistration(
    ServiceId ServiceId,
    InstanceId InstanceId,
    string BootstrapFilePath,
    string ServiceVersion);

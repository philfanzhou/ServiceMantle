using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore.PhaseGate;

/// <summary>
/// Declares the fixed set of startup phases in which an endpoint outside the management prefix
/// is admitted by the phase gate.
/// </summary>
internal sealed record PhaseAdmissionMetadata(IReadOnlySet<ServiceStartupPhase> Phases);

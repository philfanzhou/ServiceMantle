namespace ServiceMantle.ReferenceService.Discovery;

/// <summary>The explicit inputs of the sample's optional Consul registration wiring.</summary>
public static class ReferenceConsulDefaults
{
    /// <summary>The explicit activation switch. Defaults to <c>false</c>.</summary>
    public const string EnabledKey = "ReferenceService:Consul:Enabled";

    /// <summary>
    /// The optional instance identity. Defaults to <c>reference-local</c>; every instance of a
    /// multi-instance deployment must state its own value.
    /// </summary>
    public const string InstanceIdKey = "ReferenceService:InstanceId";

    /// <summary>The default instance identity used when the key is absent.</summary>
    public const string DefaultInstanceId = "reference-local";
}

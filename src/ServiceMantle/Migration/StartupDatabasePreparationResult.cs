namespace ServiceMantle.Migration;

/// <summary>The finite outcome of the startup gate's independent deployment/preparation stage.
/// Contains no target, connection string, driver diagnostic or migration receipt.</summary>
public sealed class StartupDatabasePreparationResult
{
    private StartupDatabasePreparationResult(bool succeeded, string? errorCode, bool skipped)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        Skipped = skipped;
    }

    /// <summary>Gets whether deployment validation and the enabled preparation stage succeeded.</summary>
    public bool Succeeded { get; }
    /// <summary>Gets the safe failure code, or null on success.</summary>
    public string? ErrorCode { get; }
    /// <summary>Gets whether preparation was explicitly disabled. A skipped success does not verify target existence or connectivity.</summary>
    public bool Skipped { get; }

    internal static StartupDatabasePreparationResult Success(bool skipped) => new(true, null, skipped);
    internal static StartupDatabasePreparationResult Failure(string errorCode) => new(false, errorCode, false);

    /// <summary>Returns only finite flags and the safe error code.</summary>
    public override string ToString() => $"StartupDatabasePreparationResult(Succeeded={Succeeded}, ErrorCode={ErrorCode}, Skipped={Skipped})";
}

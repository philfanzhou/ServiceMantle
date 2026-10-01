namespace ServiceMantle.Migration;

/// <summary>
/// The outcome state of one target-database schema evidence read.
/// </summary>
public enum SchemaEvidenceReadState
{
    /// <summary>The target database exists and its evidence was read completely.</summary>
    Succeeded = 0,

    /// <summary>The target database does not exist; whether that counts as empty is the
    /// consumer's classification decision.</summary>
    TargetDatabaseMissing = 1,

    /// <summary>The read failed; whether that counts as an inspection failure is the
    /// consumer's classification decision.</summary>
    ReadFailed = 2
}

/// <summary>
/// The immutable result of one target-database schema evidence read: either the complete
/// evidence (applied migration ids and structure snapshot) or one of two distinct facts —
/// the target database does not exist, or the read failed.
/// </summary>
/// <remarks>
/// <para>
/// The two failure facts stay separate because consumers classify them differently (for
/// example "missing target is an empty database" versus "inspection failed"); the result type
/// itself decides nothing.
/// </para>
/// <para>
/// Safety: <see cref="Message"/> is rendered by this type from one validated identifier and a
/// fixed English template. There is no API surface through which SQL text, connection values,
/// or exception messages could enter it; identifiers containing control characters or
/// exceeding 128 characters are rejected.
/// </para>
/// <para>
/// Non-guarantees: the result is one read's outcome, not a transactional image; whether the
/// applied migration ids and the snapshot are mutually consistent is the reader's
/// responsibility.
/// </para>
/// </remarks>
public sealed class SchemaEvidenceReadResult
{
    private SchemaEvidenceReadResult(
        SchemaEvidenceReadState state,
        IReadOnlyList<string> appliedMigrationIds,
        SchemaSnapshot? snapshot,
        string message)
    {
        State = state;
        AppliedMigrationIds = appliedMigrationIds;
        Snapshot = snapshot;
        Message = message;
    }

    /// <summary>Gets the read outcome state.</summary>
    public SchemaEvidenceReadState State { get; }

    /// <summary>
    /// Gets the applied migration ids in read order; empty unless the state is
    /// <see cref="SchemaEvidenceReadState.Succeeded"/>.
    /// </summary>
    public IReadOnlyList<string> AppliedMigrationIds { get; }

    /// <summary>
    /// Gets the structure snapshot; null unless the state is
    /// <see cref="SchemaEvidenceReadState.Succeeded"/>.
    /// </summary>
    public SchemaSnapshot? Snapshot { get; }

    /// <summary>
    /// Gets the identifier-only message describing the failed fact; empty when the read
    /// succeeded.
    /// </summary>
    public string Message { get; }

    /// <summary>Creates the successful read result with the complete evidence.</summary>
    /// <param name="appliedMigrationIds">The applied migration ids in read order.</param>
    /// <param name="snapshot">The observed structure snapshot.</param>
    public static SchemaEvidenceReadResult Success(
        IReadOnlyList<string> appliedMigrationIds,
        SchemaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(appliedMigrationIds);
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var migrationId in appliedMigrationIds)
        {
            SchemaEvidenceModel.ValidateIdentifier(migrationId, nameof(appliedMigrationIds));
        }

        return new SchemaEvidenceReadResult(
            SchemaEvidenceReadState.Succeeded,
            [.. appliedMigrationIds],
            snapshot,
            string.Empty);
    }

    /// <summary>Creates the "the target database does not exist" fact.</summary>
    /// <param name="databaseName">The target database's name, used in the message.</param>
    public static SchemaEvidenceReadResult TargetDatabaseMissing(string databaseName)
    {
        SchemaEvidenceModel.ValidateIdentifier(databaseName, nameof(databaseName));

        return new SchemaEvidenceReadResult(
            SchemaEvidenceReadState.TargetDatabaseMissing,
            [],
            null,
            $"The target database '{databaseName}' does not exist.");
    }

    /// <summary>Creates the "the read failed" fact.</summary>
    /// <param name="objectIdentifier">
    /// The identifier of the object being read when the failure was observed, used in the
    /// message.
    /// </param>
    public static SchemaEvidenceReadResult ReadFailed(string objectIdentifier)
    {
        SchemaEvidenceModel.ValidateIdentifier(objectIdentifier, nameof(objectIdentifier));

        return new SchemaEvidenceReadResult(
            SchemaEvidenceReadState.ReadFailed,
            [],
            null,
            $"The schema evidence of '{objectIdentifier}' could not be read.");
    }

    /// <summary>Returns the state and the identifier-only message.</summary>
    public override string ToString() =>
        $"SchemaEvidenceReadResult(State={State}, Message={Message})";
}

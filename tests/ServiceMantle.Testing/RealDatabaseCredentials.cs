namespace ServiceMantle.Testing;

/// <summary>
/// Supplies credentials to a real-database fixture without defining where secrets are stored.
/// </summary>
public interface IRealDatabaseCredentialSource
{
    RealDatabaseCredentials GetCredentials(RealDatabaseProvider provider);
}

/// <summary>
/// Holds credentials for fixture injection and never includes their values in its text form.
/// </summary>
public sealed class RealDatabaseCredentials
{
    public RealDatabaseCredentials(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    public override string ToString() =>
        "RealDatabaseCredentials { Username = [REDACTED], Password = [REDACTED] }";
}

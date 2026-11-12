namespace Zenkins.Server.Data.Tenancy;

/// <summary>
/// Thrown when a change would read or write data outside the active organization. It is a
/// programming/authorization error, not a transient failure: callers must not retry it, and the
/// data layer throws it before anything reaches the database.
/// </summary>
public sealed class TenantIsolationException : Exception
{
    public TenantIsolationException()
        : base("An operation crossed the active organization boundary.")
    {
    }

    public TenantIsolationException(string message)
        : base(message)
    {
    }

    public TenantIsolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

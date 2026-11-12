namespace Zenkins.Server.Data.Tenancy;

/// <summary>
/// Scoped implementation of <see cref="ISystemDataScope"/>. A scoped instance is not shared
/// between requests, so a plain counter is sufficient; disposal restores the previous state so
/// nested scopes cannot leave the flag stuck on.
/// </summary>
public sealed class SystemDataScope : ISystemDataScope
{
    private int _depth;

    /// <inheritdoc />
    public bool IsActive => _depth > 0;

    /// <inheritdoc />
    public IDisposable BeginScope()
    {
        _depth++;
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private SystemDataScope? _owner;

        public Scope(SystemDataScope owner) => _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            if (owner is not null)
            {
                owner._depth--;
            }
        }
    }
}

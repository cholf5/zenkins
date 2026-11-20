using Zenkins.Server.Runs;

namespace Zenkins.Server.Components.Shared;

/// <summary>
/// The one read behind the run page's and console page's live status (card F-047). The pages own the
/// 1s cadence; this class owns the decisions a page should not re-invent: do not read once the last
/// run is terminal, do not start a second read while one is in flight, keep the last good run when a
/// read fails, and surface the failure once per distinct exception so a flaky database does not fill
/// the log.
/// <para>
/// Polling is deliberate: the log already arrives over SignalR, and a status read every two seconds
/// is what classic Jenkins does too. It needs no new server contract and works across processes. A
/// later status hub can replace this class without changing the pages.
/// </para>
/// </summary>
public sealed class RunLiveRefresher
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<RunView?>> _read;
    private RunView? _lastGood;
    private bool _refreshing;
    private Exception? _lastError;

    public RunLiveRefresher(Func<CancellationToken, Task<RunView?>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    /// <summary>The most recent successful read (or the page's initial read), or null before one.</summary>
    public RunView? LastGood
    {
        get
        {
            lock (_gate)
            {
                return _lastGood;
            }
        }
    }

    /// <summary>Whether a read is currently in flight.</summary>
    public bool IsRefreshing
    {
        get
        {
            lock (_gate)
            {
                return _refreshing;
            }
        }
    }

    /// <summary>The most recent read failure, cleared by the next success.</summary>
    public Exception? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>Seeds the last good run with the page's initial read.</summary>
    public void Apply(RunView run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            _lastGood = run;
        }
    }

    /// <summary>
    /// One refresh attempt. Returns false when the page should stop polling (the last known run is
    /// terminal, or the caller's token fired); true means the cadence continues. A read that throws
    /// keeps <see cref="LastGood"/> and records <see cref="LastError"/>.
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_refreshing)
            {
                return true;
            }

            if (_lastGood?.IsTerminal == true)
            {
                return false;
            }

            _refreshing = true;
        }

        try
        {
            var run = await _read(cancellationToken);
            lock (_gate)
            {
                if (run is not null)
                {
                    _lastGood = run;
                }

                _lastError = null;
            }

            return run is null || !run.IsTerminal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception failure)
        {
            lock (_gate)
            {
                _lastError = failure;
            }

            return true;
        }
        finally
        {
            lock (_gate)
            {
                _refreshing = false;
            }
        }
    }
}

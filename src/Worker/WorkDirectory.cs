namespace Zenkins.Worker;

/// <summary>
/// The per-run work directory trio (card F-007, PRD §3.4.6):
/// <c>{work-root}/{run-id}/repository|artifacts|temp</c>. Every run gets its own tree; different
/// runs never share a writable directory (PRD §8 constraint 7).
/// <para>
/// <b>Run-id hardening.</b> The run id is parsed as a GUID before any path is built, and the
/// directory name is the GUID's canonical <c>D</c> rendering - so a malformed id (path traversal,
/// garbage, empty) is refused <em>before the file system is ever touched</em>, and even an exotic
/// but parseable spelling can only ever map onto the canonical, traversal-free name.
/// </para>
/// <para>
/// <b>Idempotency.</b> Preparation is plain <see cref="Directory.CreateDirectory"/>, which is a
/// no-op for existing directories - re-preparing the same run (a retried claim, a restart) neither
/// fails nor wipes anything.
/// </para>
/// </summary>
public sealed class WorkDirectoryService
{
    private readonly string _root;

    /// <param name="workRoot">
    /// The work root: <c>ZENKINS_WORK_ROOT</c>, default <c>work/</c> relative to the worker's
    /// current directory (resolved to an absolute path here; absolute values pass through).
    /// </param>
    public WorkDirectoryService(string workRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        _root = Path.GetFullPath(workRoot);
    }

    /// <summary>The absolute work root every run tree lives under.</summary>
    public string Root => _root;

    /// <summary>Whether <paramref name="runIdText"/> parses as a GUID (the only accepted shape).</summary>
    public static bool IsValidRunId(string? runIdText, out Guid runId) =>
        Guid.TryParse(runIdText, out runId);

    /// <summary>
    /// Prepares the run's directory trio, validating the run id first. A malformed run id throws
    /// <see cref="WorkDirectoryException"/> and the file system is never touched.
    /// </summary>
    public RunWorkDirectory Prepare(string runIdText)
    {
        if (!IsValidRunId(runIdText, out var runId))
        {
            throw new WorkDirectoryException(
                $"run id '{runIdText}' is not a GUID; refusing to touch the file system.");
        }

        return Prepare(runId);
    }

    /// <summary>Prepares (idempotently) the trio for an already-validated run id.</summary>
    public RunWorkDirectory Prepare(Guid runId)
    {
        var rootPath = Path.Combine(_root, runId.ToString("D"));
        var work = new RunWorkDirectory(
            runId,
            rootPath,
            Path.Combine(rootPath, "repository"),
            Path.Combine(rootPath, "artifacts"),
            Path.Combine(rootPath, "temp"));
        Directory.CreateDirectory(work.RepositoryPath);
        Directory.CreateDirectory(work.ArtifactsPath);
        Directory.CreateDirectory(work.TempPath);
        return work;
    }

    /// <summary>
    /// Prepares a probe's scratch tree (card I-005): <c>{work-root}/probes/{probe-id}/temp</c> and
    /// <c>.../hardening</c>. A probe is not a run - it has no repository, no artifacts and no claim - so
    /// it gets its own two directories under the same root, and the probe deletes the whole tree when it
    /// is done: a probe leaves nothing behind but its answer.
    /// <para>
    /// The id is a GUID (the Server's probe id), so the same canonical-name discipline as
    /// <see cref="Prepare(Guid)"/> applies: nothing here can escape the work root.
    /// </para>
    /// </summary>
    public ProbeWorkDirectory PrepareProbe(Guid probeId)
    {
        var rootPath = Path.Combine(_root, "probes", probeId.ToString("D"));
        var probe = new ProbeWorkDirectory(
            probeId,
            rootPath,
            Path.Combine(rootPath, "temp"),
            Path.Combine(rootPath, "hardening"));
        Directory.CreateDirectory(probe.TempPath);
        Directory.CreateDirectory(probe.HardeningPath);
        return probe;
    }

    /// <summary>
    /// Deletes a probe's whole scratch tree. Missing directories are fine; a failure to delete is not the
    /// probe's outcome and is left to the caller to log.
    /// </summary>
    public void CleanupProbe(ProbeWorkDirectory probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (Directory.Exists(probe.RootPath))
        {
            Directory.Delete(probe.RootPath, recursive: true);
        }
    }

    /// <summary>
    /// Deletes the run's <c>temp/</c> tree (card decision: on by default via
    /// <c>ZENKINS_CLEANUP_TEMP</c>). <c>repository/</c> and <c>artifacts/</c> are kept; retention
    /// for those is a later card. Deleting a missing directory is fine.
    /// </summary>
    public void CleanupTemp(RunWorkDirectory work)
    {
        if (Directory.Exists(work.TempPath))
        {
            Directory.Delete(work.TempPath, recursive: true);
        }
    }
}

/// <summary>The prepared trio of one run. Paths are absolute.</summary>
/// <param name="RunId">The run this tree belongs to.</param>
/// <param name="RootPath">The run's own directory (<c>work/{run-id}</c>).</param>
/// <param name="RepositoryPath">The checkout (<c>repository/</c>), kept after the run.</param>
/// <param name="ArtifactsPath">Published artifacts (<c>artifacts/</c>), kept after the run.</param>
/// <param name="TempPath">Scratch space (<c>temp/</c>): credential carrier files, known-hosts, logs,
/// deleted after the run when the worker is configured to clean up.</param>
public sealed record RunWorkDirectory(
    Guid RunId,
    string RootPath,
    string RepositoryPath,
    string ArtifactsPath,
    string TempPath)
{
    /// <summary>
    /// The run's git/ssh hardening directory (card F-018): the empty <c>gitconfig</c> and
    /// <c>ssh_config</c> files every git child is pointed at. Deliberately beside <c>temp/</c> rather
    /// than inside it - it holds no credential and no output, and <c>temp/</c>'s emptiness after a
    /// run is a property the credential-secrecy tests rely on.
    /// </summary>
    public string HardeningPath => Path.Combine(RootPath, "hardening");

    /// <summary>
    /// The run's workspace (card F-020): the working directory a <b>Freestyle</b> run's steps execute
    /// in and the root its declared artifacts are resolved against. Unlike the trio above it is not
    /// created by <see cref="WorkDirectoryService.Prepare"/> - a Pipeline run never touches it, so the
    /// prepared trio stays exactly PRD §3.4.6's; the run loop creates it only when the claim carries a
    /// frozen spec, and preparation is idempotent like every directory here.
    /// </summary>
    public string WorkspacePath => Path.Combine(RootPath, "workspace");
}

/// <summary>
/// One probe's scratch tree (card I-005): the credential carrier's <c>temp/</c> and the git/ssh hardening
/// files' <c>hardening/</c>. Deliberately <b>not</b> a <see cref="RunWorkDirectory"/>: a probe clones
/// nothing and publishes nothing, so it must not be able to look like a run to any code that walks runs.
/// </summary>
/// <param name="ProbeId">The Server's probe id.</param>
/// <param name="RootPath">The probe's own directory (<c>work/probes/{probe-id}</c>).</param>
/// <param name="TempPath">Where the credential files are materialised (deleted with them).</param>
/// <param name="HardeningPath">The empty git/ssh config files every git child is pointed at.</param>
public sealed record ProbeWorkDirectory(
    Guid ProbeId,
    string RootPath,
    string TempPath,
    string HardeningPath);

/// <summary>A refused or failed work-directory operation; always safe to log verbatim.</summary>
public sealed class WorkDirectoryException(string message) : Exception(message);

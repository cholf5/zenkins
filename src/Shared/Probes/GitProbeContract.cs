namespace Zenkins.Shared.Probes;

/// <summary>
/// The closed vocabulary of a Git remote probe's answer (card I-005): the two shapes it can take, and
/// the four reasons it can fail. Shared by the Server and the Worker because the same strings cross the
/// wire twice - the Worker reports one, the Server records it on the probe and on the project, and both
/// sides validate against this list.
/// <para>
/// <b>Why a failure is a named outcome and never an empty ref list.</b> Branch discovery (F-023) and SCM
/// polling (F-025) both ask "what heads exist right now?". A remote the worker could not reach is not a
/// remote with no branches: interpreting it as "no heads" would make every branch look deleted. The
/// outcomes below keep the two apart by construction - <see cref="Refs"/> is the only outcome that
/// carries refs, and every other value is a failure a caller must not treat as "nothing changed".
/// </para>
/// </summary>
public static class GitProbeOutcomes
{
    /// <summary>The probe succeeded: <see cref="GitProbeRefs.Heads"/> is the remote's current heads.</summary>
    public const string Refs = "refs";

    /// <summary>The remote refused the project's credential (a wrong token, a rejected key).</summary>
    public const string AuthRejected = "git-auth-rejected";

    /// <summary>The remote could not be reached at all (DNS, connection, TLS).</summary>
    public const string Unreachable = "git-unreachable";

    /// <summary>One git invocation exceeded its budget and its process tree was killed.</summary>
    public const string Timeout = "git-timeout";

    /// <summary>
    /// Any other git failure the worker could classify no better: no git binary on the machine, a URL
    /// that is not a repository, a remote that answered something none of the three above describe.
    /// The message says what happened; the value is deliberately <b>not</b> one of the three typed
    /// failures, so a caller that only understands those three does not silently pretend it knows.
    /// </summary>
    public const string Failed = "git-failed";

    /// <summary>
    /// Nobody claimed the probe inside the Server's bounded wait (no probe-capable Worker registered, or
    /// each claimant lost its lease). The Server settles the probe with this value itself - it is never
    /// reported by a worker.
    /// </summary>
    public const string NoWorker = "no-worker";

    /// <summary>Every value a probe outcome can carry.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Refs, AuthRejected, Unreachable, Timeout, Failed, NoWorker];

    /// <summary>Whether <paramref name="outcome"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? outcome) =>
        outcome is not null && All.Contains(outcome, StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="outcome"/> is the success shape. Every other known outcome is a failure a
    /// caller must record and surface - never read as "no change".
    /// </summary>
    public static bool IsSuccess(string? outcome) => string.Equals(outcome, Refs, StringComparison.Ordinal);

    /// <summary>Whether a worker may report this outcome (everything but <see cref="NoWorker"/>).</summary>
    public static bool IsWorkerReportable(string? outcome) =>
        IsKnown(outcome) && !string.Equals(outcome, NoWorker, StringComparison.Ordinal);
}

/// <summary>One head of the probed remote: the full ref name and the commit it points at.</summary>
/// <param name="Name">The full ref name (<c>refs/heads/main</c>), never a short label.</param>
/// <param name="Sha">The commit's object name (40 hex for SHA-1, 64 for SHA-256).</param>
public sealed record GitProbeRef(string Name, string Sha);

/// <summary>
/// The success payload of a probe: every <c>refs/heads/*</c> of the remote, plus what the remote's
/// <c>HEAD</c> resolves to.
/// <para>
/// <b>Heads are full ref names.</b> F-023 needs the name it will read back; a short label would make the
/// consumer re-derive the prefix and drift from this contract.
/// </para>
/// <para>
/// <b>An empty repository is a success, not a failure.</b> <see cref="Heads"/> may be empty and
/// <see cref="HeadSha"/> <see langword="null"/> (a bare repository with no commit has no HEAD object) -
/// that is the honest answer to "which heads exist", and it is distinguishable from every failure value.
/// </para>
/// </summary>
/// <param name="Heads">The heads, ordered by name (ordinal).</param>
/// <param name="HeadSha">The commit <c>HEAD</c> points at, or <see langword="null"/> when the remote has none.</param>
/// <param name="HeadBranch">The branch <c>HEAD</c> is a symbolic ref to (<c>refs/heads/main</c>), or
/// <see langword="null"/> when the remote does not advertise it (a detached HEAD, or an empty repository).</param>
public sealed record GitProbeRefs(
    IReadOnlyList<GitProbeRef> Heads,
    string? HeadSha = null,
    string? HeadBranch = null)
{
    /// <summary>The empty answer: no heads, no HEAD. A legitimate success for an empty repository.</summary>
    public static GitProbeRefs Empty { get; } = new([]);
}

/// <summary>
/// The shape rules a probe answer must satisfy on both sides of the wire (card I-005).
/// <para>
/// <b>One set of rules, two callers.</b> The Worker checks its own answer before reporting (so a remote
/// with an implausible number of heads is one clear <c>git-failed</c> line, never a 400 the worker cannot
/// act on), and the Server re-validates every report it receives (it must not trust a worker binary).
/// The caps exist because every probe's answer is held in memory for its dedupe window.
/// </para>
/// </summary>
public static class GitProbeRules
{
    /// <summary>The longest ref name a probe accepts.</summary>
    public const int MaxRefNameLength = 255;

    /// <summary>The longest diagnostic message a report may carry.</summary>
    public const int MaxMessageLength = 500;

    /// <summary>
    /// The most heads one probe may report. A repository with more branches than this is not what
    /// multibranch discovery is for, and the cap keeps one probe's answer bounded in memory.
    /// </summary>
    public const int MaxHeads = 10_000;

    /// <summary>The prefix every reported head must carry (the full ref name, not a short label).</summary>
    public const string HeadPrefix = "refs/heads/";

    /// <summary>Whether <paramref name="name"/> is a usable full head ref name.</summary>
    public static bool IsHeadName(string? name) =>
        name is { Length: > 11 and <= MaxRefNameLength }
        && name.StartsWith(HeadPrefix, StringComparison.Ordinal)
        && !name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    /// <summary>Whether <paramref name="sha"/> looks like a git object name (lower-case hex, 40 or 64).</summary>
    public static bool IsObjectName(string? sha) =>
        sha is { Length: 40 or 64 }
        && sha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Validates one report's fields. Returns <see langword="null"/> when it is acceptable, otherwise a
    /// stable error code (the endpoint answers it as a 400).
    /// </summary>
    /// <param name="outcome">The reported outcome (must be worker-reportable).</param>
    /// <param name="heads">The reported heads (empty for a failure outcome).</param>
    /// <param name="headSha">The reported remote HEAD object name, or null.</param>
    /// <param name="headBranch">The reported symbolic HEAD target, or null.</param>
    /// <param name="message">The optional diagnostic.</param>
    /// <returns><see langword="null"/> when acceptable, otherwise a <see cref="GitProbeErrorCodes"/> value.</returns>
    public static string? ValidateReport(
        string? outcome,
        IReadOnlyList<GitProbeRef>? heads,
        string? headSha,
        string? headBranch,
        string? message)
    {
        if (!GitProbeOutcomes.IsWorkerReportable(outcome))
        {
            return GitProbeErrorCodes.OutcomeInvalid;
        }

        var reported = heads ?? [];
        if (reported.Count > MaxHeads)
        {
            return GitProbeErrorCodes.TooManyHeads;
        }

        foreach (var head in reported)
        {
            if (!IsHeadName(head.Name) || !IsObjectName(head.Sha))
            {
                return GitProbeErrorCodes.RefInvalid;
            }
        }

        if (headSha is not null && !IsObjectName(headSha))
        {
            return GitProbeErrorCodes.HeadInvalid;
        }

        if (headBranch is not null && !IsHeadName(headBranch))
        {
            return GitProbeErrorCodes.HeadInvalid;
        }

        if (message is { Length: > MaxMessageLength })
        {
            return GitProbeErrorCodes.MessageTooLong;
        }

        // The success shape is the only one that carries refs: a failure that also carried heads would
        // make "the remote refused us, and here are its branches" a storable lie. A HEAD object that no
        // head carries is legitimate (a detached HEAD), which is why the reverse is not checked.
        if (!GitProbeOutcomes.IsSuccess(outcome)
            && (reported.Count != 0 || headSha is not null || headBranch is not null))
        {
            return GitProbeErrorCodes.RefsOnFailure;
        }

        return null;
    }
}

/// <summary>The stable error codes a probe report or request can be refused with.</summary>
public static class GitProbeErrorCodes
{
    /// <summary>The reported outcome is not one a worker may report.</summary>
    public const string OutcomeInvalid = "probe-outcome-invalid";

    /// <summary>One reported head is not a full <c>refs/heads/…</c> name with a hex object name.</summary>
    public const string RefInvalid = "probe-ref-invalid";

    /// <summary>The reported remote HEAD is not an object name / not a full head ref name.</summary>
    public const string HeadInvalid = "probe-head-invalid";

    /// <summary>The report carried more heads than <see cref="GitProbeRules.MaxHeads"/>.</summary>
    public const string TooManyHeads = "probe-heads-too-many";

    /// <summary>The diagnostic message is longer than <see cref="GitProbeRules.MaxMessageLength"/>.</summary>
    public const string MessageTooLong = "probe-message-too-long";

    /// <summary>A failure outcome carried refs (only <c>refs</c> may).</summary>
    public const string RefsOnFailure = "probe-refs-on-failure";

    /// <summary>The project the probe was requested for has no repository (a Freestyle project).</summary>
    public const string ProjectHasNoRepository = "project-has-no-repository";

    /// <summary>The probe is no longer this worker's: reported, abandoned or re-leased.</summary>
    public const string LeaseLost = "probe-lease-lost";

    /// <summary>The probe was already answered (or settled by the Server) before this report arrived.</summary>
    public const string AlreadySettled = "probe-already-settled";
}

/// <summary>
/// The registration/heartbeat capability flag (card I-005): whether a Worker can answer probes.
/// <para>
/// <b>Absent means no.</b> A Worker built before this card does not send the field, so the Server must
/// read "absent" as <see langword="false"/> - never as "unknown, hand it probes anyway". That is what
/// makes an upgrade skippable: old workers keep building runs, and only new ones are asked for refs.
/// </para>
/// </summary>
public static class GitProbeCapability
{
    /// <summary>The wire field name both the registration and the heartbeat payloads carry.</summary>
    public const string FieldName = "canProbe";
}

using Zenkins.Worker.Lua;

namespace Zenkins.Worker.Git;

/// <summary>
/// <c>checkout()</c> as a Lua host primitive (card F-013, registered through F-011's
/// <see cref="ILuaRuntime.RegisterHostFunction"/> seam).
/// <para>
/// <b>It suspends; it does not block the script.</b> The registered callback only reads its argument
/// and hands the driver a <see cref="LuaHostResult.Suspend"/> - the coroutine parks, the driver
/// invokes the answer (the git work) on the owning thread, and the script resumes with the typed
/// table the answer returns (<c>docs/lua-runtime.md</c> §5.2). Nothing sleeps or waits inside the Lua
/// callback itself, so a long checkout never looks like a hung script and cannot starve the
/// instruction-budget/cancellation observations.
/// </para>
/// <para>
/// <b>Arguments are scalars.</b> The seam delivers a table argument as <c>nil</c>, so the primitive
/// takes a single string: the commit to check out, or nothing for the run's pinned commit. The answer
/// is a table (<c>commit</c>, <c>already_at_commit</c>, and <c>error</c> when the operation failed),
/// because a table is what the host may hand back across the seam - never a CLR object.
/// </para>
/// </summary>
public sealed class GitCheckoutHostPrimitive
{
    /// <summary>The SDK function name the pipeline scripts call.</summary>
    public const string FunctionName = "checkout";

    private const string CommitKey = "commit";
    private const string AlreadyKey = "already_at_commit";
    private const string ErrorKey = "error";

    private readonly Func<string?, CancellationToken, Task<GitCheckoutResult>> _checkout;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Binds the primitive to one run's workspace.</summary>
    /// <param name="workspace">The run's workspace (already cloned, or cloning is still to come).</param>
    /// <param name="cancellationToken">The run's cancellation token (F-009's kill path feeds it).</param>
    public GitCheckoutHostPrimitive(GitWorkspace workspace, CancellationToken cancellationToken = default)
        : this(
            (workspace ?? throw new ArgumentNullException(nameof(workspace))).CheckoutAsync,
            cancellationToken)
    {
    }

    /// <summary>
    /// Binds the primitive to an arbitrary operation. Exists so the suspension contract can be tested
    /// without a repository, and so F-014 can supply a workspace whose lifetime it owns.
    /// </summary>
    public GitCheckoutHostPrimitive(
        Func<string?, CancellationToken, Task<GitCheckoutResult>> checkout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkout);
        _checkout = checkout;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Registers <c>checkout</c> on the runtime. Call before the first execution.</summary>
    public void Register(ILuaRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        runtime.RegisterHostFunction(FunctionName, Invoke);
    }

    /// <summary>
    /// The registered primitive: validates the argument, and either answers immediately (a malformed
    /// argument) or suspends with the real checkout as its answer.
    /// </summary>
    public LuaHostResult Invoke(LuaHostCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        string? requested;
        switch (call.FirstArgument)
        {
            case null:
                requested = null;
                break;

            case { Kind: LuaHostValueKind.Nil }:
                requested = null;
                break;

            case { Kind: LuaHostValueKind.String } value:
                requested = value.Text;
                break;

            default:
                // A number/boolean/table argument is a programming error in the script: answer
                // immediately instead of pretending to have checked something out.
                return LuaHostResult.Value(
                    LuaHostValue.FromTable(LuaHostTable.Map(
                        (ErrorKey, LuaHostValue.FromString(
                            "checkout() takes a commit sha string (or nothing for the run's pinned commit).")),
                        (AlreadyKey, LuaHostValue.FromBoolean(false)))));
        }

        return LuaHostResult.Suspend(() => Answer(requested));
    }

    /// <summary>
    /// The suspended work: runs the checkout and answers with the table the script resumes with. This
    /// runs on the driver's thread after the coroutine has parked, which is the one place the seam
    /// allows a blocking wait (<c>docs/lua-runtime.md</c> §5.2).
    /// </summary>
    private LuaHostValue Answer(string? requested)
    {
        try
        {
            var result = _checkout(requested, _cancellationToken).GetAwaiter().GetResult();
            return LuaHostValue.FromTable(LuaHostTable.Map(
                (CommitKey, LuaHostValue.FromString(result.Commit)),
                (AlreadyKey, LuaHostValue.FromBoolean(result.AlreadyAtCommit))));
        }
        catch (GitException failure)
        {
            // The message is already credential-free: it comes from the sanitised URL and the
            // redacted stderr tail.
            return LuaHostValue.FromTable(LuaHostTable.Map(
                (CommitKey, LuaHostValue.FromString(string.Empty)),
                (AlreadyKey, LuaHostValue.FromBoolean(false)),
                (ErrorKey, LuaHostValue.FromString(failure.Message))));
        }
    }
}

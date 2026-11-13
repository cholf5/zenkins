using Xunit;
using Zenkins.Worker.Lua;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// TC-004 plus the seam contract (card F-013, AC-6): <c>checkout()</c> is idempotent and pinned, and it
/// is a real host-primitive suspension - the script parks while the work runs, and the driver resumes
/// it with the typed answer (<c>docs/lua-runtime.md</c> §5.2), never a blocking wait inside the Lua
/// callback.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCheckoutTests
{
    [Fact]
    public async Task Checkout_is_a_noop_when_already_at_the_pinned_commit()
    {
        using var fixture = GitFixtureRepository.Create("checkout-noop");
        using var directory = new GitTestDirectory("checkout-noop-run");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment));

        var clone = await workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken);

        var traceBefore = workspace.Trace.Count;
        var result = await workspace.CheckoutAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.AlreadyAtCommit);
        Assert.Equal(clone.Commit, result.Commit);

        // Idempotence is measured on the command trace: no fetch, no checkout - only the read that
        // decides the workspace is already where it should be.
        var newEntries = workspace.Trace.Skip(traceBefore).ToArray();
        Assert.Single(newEntries);
        Assert.StartsWith("rev-parse", newEntries[0].Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(newEntries, entry => entry.Arguments.StartsWith("fetch", StringComparison.Ordinal));
        Assert.DoesNotContain(newEntries, entry => entry.Arguments.StartsWith("checkout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checkout_returns_to_the_pinned_commit_after_HEAD_moved()
    {
        using var fixture = GitFixtureRepository.Create("checkout-return");
        using var directory = new GitTestDirectory("checkout-return-run");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment));

        var clone = await workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken);

        // Move HEAD somewhere unrelated (the first commit), as a misbehaving step would.
        GitCli.MustRun(work.RepositoryPath, fixture.Environment, "checkout", "--detach", fixture.FirstMainCommit);
        Assert.Equal(
            fixture.FirstMainCommit,
            GitCli.RevParse(work.RepositoryPath, fixture.Environment, "HEAD"));

        var traceBefore = workspace.Trace.Count;
        var result = await workspace.CheckoutAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.AlreadyAtCommit);
        Assert.Equal(clone.Commit, result.Commit);
        Assert.Equal(
            clone.Commit,
            GitCli.RevParse(work.RepositoryPath, fixture.Environment, "HEAD"));

        // The tree really is the pinned one, not just HEAD: the file content matches the commit.
        Assert.Equal("third\n", await File.ReadAllTextAsync(
            Path.Combine(work.RepositoryPath, "src", "app.txt"),
            TestContext.Current.CancellationToken));

        var newEntries = workspace.Trace.Skip(traceBefore).ToArray();
        Assert.Contains(newEntries, entry => entry.Arguments.StartsWith("fetch", StringComparison.Ordinal));
        Assert.Contains(newEntries, entry => entry.Arguments.StartsWith("checkout --detach --force", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checkout_refuses_a_branch_name_as_a_commit()
    {
        using var fixture = GitFixtureRepository.Create("checkout-branch");
        using var directory = new GitTestDirectory("checkout-branch-run");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment));

        await workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(
            () => workspace.CheckoutAsync("main", TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.Failed, failure.Kind);
        Assert.Contains("main", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_primitive_returns_a_suspension_instead_of_answering_inline()
    {
        var primitive = new WorkerGit.GitCheckoutHostPrimitive(
            (commit, _) =>
            {
                Assert.Equal("abc1234", commit);
                return Task.FromResult(new WorkerGit.GitCheckoutResult("abc1234", AlreadyAtCommit: true));
            });

        var pending = primitive.Invoke(new LuaHostCall(
            WorkerGit.GitCheckoutHostPrimitive.FunctionName,
            [LuaHostValue.FromString("abc1234")]));

        // The Lua callback parked; the work has not happened yet (its assertions run in the answer).
        Assert.True(pending.IsSuspended);

        var answer = pending.ResolveAnswer();
        Assert.Equal(LuaHostValueKind.Table, answer.Kind);
        Assert.Equal("abc1234", answer.Table.Entries.Single(entry => entry.Key == "commit").Value.Text);
        Assert.True(answer.Table.Entries.Single(entry => entry.Key == "already_at_commit").Value.Boolean);
    }

    [Fact]
    public void A_non_string_argument_is_answered_without_suspending()
    {
        var primitive = new WorkerGit.GitCheckoutHostPrimitive(
            (_, _) => throw new InvalidOperationException("the operation must not run for a bad argument"));

        var answer = primitive.Invoke(new LuaHostCall(
            WorkerGit.GitCheckoutHostPrimitive.FunctionName,
            [LuaHostValue.FromNumber(42)]));

        Assert.False(answer.IsSuspended);
        Assert.Contains(
            answer.ImmediateValue.Table.Entries,
            entry => entry.Key == "error" && entry.Value.Text.Contains("sha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_script_is_suspended_while_checkout_works_and_resumes_with_the_answer()
    {
        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorkerGit.GitCheckoutResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var primitive = new WorkerGit.GitCheckoutHostPrimitive(
            (commit, cancellationToken) =>
            {
                Assert.Null(commit);
                Assert.False(cancellationToken.IsCancellationRequested);
                workStarted.TrySetResult();
                return release.Task;
            });
        primitive.Register(runtime);

        var execution = Task.Run(
            () => runtime.Execute(
                "local r = checkout()\n"
                + "print('commit=' .. tostring(r.commit))\n"
                + "print('already=' .. tostring(r.already_at_commit))\n"
                + "return 'checked-out'",
                "pipeline.lua",
                DateTimeOffset.MaxValue,
                CancellationToken.None),
            TestContext.Current.CancellationToken);

        // The host is doing the work and the script has not advanced past the call: it is parked in the
        // suspension, not blocked inside the Lua callback and not resumed early.
        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(execution.IsCompleted, "the execution finished before the checkout work did");
        Assert.Empty(sink.Lines);

        release.SetResult(new WorkerGit.GitCheckoutResult("0123456789abcdef", AlreadyAtCommit: false));

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("checked-out", result.Value);
        Assert.Equal(
            ["commit=0123456789abcdef", "already=false"],
            sink.Lines);
    }

    [Fact]
    public async Task A_failed_checkout_resumes_the_script_with_an_error_field()
    {
        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);
        var primitive = new WorkerGit.GitCheckoutHostPrimitive(
            (_, _) => throw new WorkerGit.GitException(
                "could not check out commit '0123456' (git exit 128: nope).",
                WorkerGit.GitFailureKind.Failed));
        primitive.Register(runtime);

        var result = runtime.Execute(
            "local r = checkout('0123456')\n"
            + "print('error=' .. tostring(r.error))\n"
            + "return 'handled'",
            "pipeline.lua",
            DateTimeOffset.MaxValue,
            CancellationToken.None);

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("handled", result.Value);
        Assert.Contains(sink.Lines, line => line.StartsWith("error=could not check out commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_real_workspace_backed_primitive_checks_out_the_pinned_commit()
    {
        using var fixture = GitFixtureRepository.Create("checkout-lua-e2e");
        using var directory = new GitTestDirectory("checkout-lua-e2e-run");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment));

        var clone = await workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken);

        // Detach HEAD before the script runs: the primitive must bring it back.
        GitCli.MustRun(work.RepositoryPath, fixture.Environment, "checkout", "--detach", fixture.FirstMainCommit);

        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);
        new WorkerGit.GitCheckoutHostPrimitive(workspace).Register(runtime);

        var result = runtime.Execute(
            "local r = checkout()\n"
            + "print('commit=' .. tostring(r.commit))\n"
            + "print('already=' .. tostring(r.already_at_commit))\n"
            + "return r.commit",
            "pipeline.lua",
            DateTimeOffset.UtcNow.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(clone.Commit, result.Value);
        Assert.Contains($"commit={clone.Commit}", sink.Lines);
        Assert.Contains("already=false", sink.Lines);
        Assert.Equal(
            clone.Commit,
            GitCli.RevParse(work.RepositoryPath, fixture.Environment, "HEAD"));
    }
}

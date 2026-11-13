namespace Zenkins.Tests.Webhooks;

/// <summary>
/// The three push shapes the endpoint must read (card F-017): GitHub, Gitea and GitLab. Each carries the
/// same three fields (<c>ref</c>, <c>before</c>, <c>after</c>) plus provider-specific noise, and each names
/// a <c>clone_url</c> that is <b>not</b> the project's repository - the delivery tests use that to prove the
/// payload can never change where a run clones from.
/// </summary>
internal static class WebhookPayloads
{
    /// <summary>A syntactically valid 40-hex object name (never a real commit; nothing clones in tests).</summary>
    public const string Before = "1111111111111111111111111111111111111111";

    /// <summary>The all-zero object name git sends for a delete event.</summary>
    public static readonly string Deleted = new('0', 40);

    /// <summary>A commit name a test can point at; <paramref name="seed"/> is a single hex digit.</summary>
    public static string Commit(char seed) => new(seed is >= '0' and <= '9' or >= 'a' and <= 'f' ? seed : 'a', 40);

    /// <summary>GitHub's push payload (the <c>X-GitHub-Event: push</c> body).</summary>
    public static string GitHub(string branch, string after, string cloneUrl = "https://evil.example/attacker.git") =>
        $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "before": "{{Before}}",
          "after": "{{after}}",
          "created": false,
          "deleted": false,
          "forced": false,
          "repository": {
            "id": 1296269,
            "name": "attacker",
            "full_name": "evil/attacker",
            "clone_url": "{{cloneUrl}}",
            "ssh_url": "git@evil.example:evil/attacker.git"
          },
          "pusher": { "name": "someone", "email": "someone@example.test" },
          "head_commit": { "id": "{{after}}", "message": "push" }
        }
        """;

    /// <summary>Gitea's push payload (the <c>X-Gitea-Event</c> body; same three fields).</summary>
    public static string Gitea(string branch, string after, string cloneUrl = "https://evil.example/attacker.git") =>
        $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "before": "{{Before}}",
          "after": "{{after}}",
          "compare_url": "https://evil.example/evil/attacker/compare",
          "repository": {
            "name": "attacker",
            "full_name": "evil/attacker",
            "clone_url": "{{cloneUrl}}"
          },
          "commits": [{ "id": "{{after}}", "message": "push" }],
          "pusher": { "login": "someone" }
        }
        """;

    /// <summary>
    /// GitLab's push payload (the <c>X-Gitlab-Event: Push Hook</c> body). GitLab names the repository URL as
    /// <c>git_http_url</c>, so this shape is also the "no clone_url at all" case.
    /// </summary>
    public static string GitLab(string branch, string after) =>
        $$"""
        {
          "object_kind": "push",
          "event_name": "push",
          "before": "{{Before}}",
          "after": "{{after}}",
          "ref": "refs/heads/{{branch}}",
          "checkout_sha": "{{after}}",
          "project": {
            "id": 4242,
            "name": "attacker",
            "path_with_namespace": "evil/attacker",
            "git_http_url": "https://evil.example/evil/attacker.git"
          },
          "commits": [{ "id": "{{after}}", "message": "push" }],
          "total_commits_count": 1
        }
        """;

    /// <summary>A payload with a ref but no <c>after</c> (a 400).</summary>
    public static string WithoutAfter(string branch) => $$"""{ "ref": "refs/heads/{{branch}}" }""";

    /// <summary>A payload with an <c>after</c> but no <c>ref</c> (a 400).</summary>
    public static string WithoutRef(string after) => $$"""{ "after": "{{after}}" }""";

    /// <summary>A ref that is not a branch (a tag push): accepted and ignored, never a run.</summary>
    public static string Tag(string after) =>
        $$"""
        {
          "ref": "refs/tags/v1.0.0",
          "before": "{{Before}}",
          "after": "{{after}}"
        }
        """;
}

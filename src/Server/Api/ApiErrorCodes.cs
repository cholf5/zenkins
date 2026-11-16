namespace Zenkins.Server.Api;

/// <summary>
/// The stable refusal codes of the organization API (card F-027). One vocabulary for the JSON
/// <c>error</c> field and the token-management forms' <c>?error=</c>, so a script and a browser read the
/// same word for the same refusal. Codes that a run trigger or a run resource can answer with are
/// <b>not</b> repeated here: those are <c>RunErrorCodes</c> and <c>ProjectTriggerRefusals</c>, reused
/// verbatim because the API must answer exactly what the form answers (the card's central rule).
/// </summary>
public static class ApiErrorCodes
{
    /// <summary>The token's label is required.</summary>
    public const string NameRequired = "name-required";

    /// <summary>The token's label is too long or carries a control character.</summary>
    public const string NameInvalid = "name-invalid";

    /// <summary>The organization already has a token with this label.</summary>
    public const string NameTaken = "name-taken";

    /// <summary>A minted token must carry at least one of <c>trigger</c>, <c>read</c>, <c>cancel</c>.</summary>
    public const string ScopeRequired = "scope-required";

    /// <summary>A requested scope is not one of the known three.</summary>
    public const string ScopeInvalid = "scope-invalid";

    /// <summary>The token was already revoked; revoking again changes nothing.</summary>
    public const string TokenAlreadyRevoked = "token-already-revoked";

    /// <summary>The value of the <c>Idempotency-Key</c> header is blank or longer than the bound.</summary>
    public const string IdempotencyKeyInvalid = "idempotency-key-invalid";

    /// <summary>The token's per-minute budget is spent (429).</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The <c>?from=</c> log offset is not a non-negative whole number.</summary>
    public const string FromInvalid = "from-invalid";

    /// <summary>The <c>?page=</c>/<c>?pageSize=</c> values are not whole numbers inside their bounds.</summary>
    public const string PageInvalid = "page-invalid";

    /// <summary>The request body is missing or does not parse (e.g. an unreadable JSON document).</summary>
    public const string RequestInvalid = "request-invalid";
}

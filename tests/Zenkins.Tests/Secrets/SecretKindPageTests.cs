using Xunit;
using Zenkins.Server.Secrets;
using Zenkins.Shared.Secrets;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// Card F-040's page half: the Secrets page can create each kind, shows which kind a row is with the syntax
/// that reads it, and asks a pair's row for both halves when rotating. Still write-only: no control renders a
/// stored value, and the page states the one thing that is deliberately <b>not</b> redacted (a file's path).
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretKindPageTests
{
    private const string PairName = "PAGE_REGISTRY";
    private const string PairUser = "page-bot";
    private const string PairPassword = "page-pair-pw-5b8d";
    private const string FileName = "PAGE_DEPLOY_KEY";
    private const string FileContent = "page-key-material-0e7c";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_create_form_offers_the_kind_and_its_fields_and_states_the_grammar()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = PairName,
                ["Kind"] = SecretKinds.UsernamePassword,
                ["Username"] = PairUser,
                ["Password"] = PairPassword,
            });
        await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = FileName,
                ["Kind"] = SecretKinds.File,
                ["Value"] = FileContent,
            });

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        // The create form carries a kind selector and the fields each kind needs; the value fields are password
        // inputs, so a stored value can never be rendered back into one.
        Assert.Contains("name=\"Kind\"", page, StringComparison.Ordinal);
        Assert.Contains($"value=\"{SecretKinds.UsernamePassword}\"", page, StringComparison.Ordinal);
        Assert.Contains($"value=\"{SecretKinds.File}\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Value\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Username\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Password\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"value=\"{PairPassword}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"value=\"{FileContent}\"", page, StringComparison.Ordinal);

        // A row says which kind it is with the syntax that reads it - the lookup an author needs.
        Assert.Contains("Username/password", page, StringComparison.Ordinal);
        Assert.Contains($"${{secret:{PairName}.username}}", page, StringComparison.Ordinal);
        Assert.Contains($"${{secretFile:{FileName}}}", page, StringComparison.Ordinal);

        // And the page is explicit that a file's path is safe to print while its contents are not.
        Assert.Contains("path", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is not", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pair_row_rotates_both_halves_while_a_text_row_rotates_one_value()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = PairName,
                ["Kind"] = SecretKinds.UsernamePassword,
                ["Username"] = PairUser,
                ["Password"] = PairPassword,
            });
        var text = await host.CreateSecretAsync(owner, organizationId, "PAGE_TEXT", "page-text-value-3a1d");

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        // The pair's rotate form is POSTed to the pair's own route and asks for a username and a password.
        var pairForm = Between(page, $"/secrets/{await PairIdAsync(host, organizationId)}/rotate", "</form>");
        Assert.Contains("name=\"Username\"", pairForm, StringComparison.Ordinal);
        Assert.Contains("name=\"Password\"", pairForm, StringComparison.Ordinal);

        // The text row keeps the single value field it always had.
        var textForm = Between(page, $"/secrets/{text.Id}/rotate", "</form>");
        Assert.Contains("name=\"Value\"", textForm, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Password\"", textForm, StringComparison.Ordinal);
    }

    private static async Task<Guid> PairIdAsync(SecretsTestHost host, Guid organizationId) =>
        (await host.ReadSecretByNameAsync(organizationId, PairName))!.Id;

    /// <summary>The markup from <paramref name="start"/> up to the first <paramref name="terminator"/>.</summary>
    private static string Between(string page, string start, string terminator)
    {
        var from = page.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' does not appear on the page.");
        var to = page.IndexOf(terminator, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{terminator}' does not appear after '{start}'.");
        return page[from..to];
    }
}

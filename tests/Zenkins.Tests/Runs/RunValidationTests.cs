using Xunit;
using Zenkins.Server.Runs;

namespace Zenkins.Tests.Runs;

/// <summary>
/// The pure trigger-form rules (cards F-006 + F-014): the branch a run builds (blank = the project's
/// default, otherwise a valid git ref), and tag parsing/covering. The argv rules this file used to pin
/// went away with the argv trigger itself.
/// </summary>
[Trait("Category", "Runs")]
public sealed class RunValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_branch_means_the_projects_default(string? branch)
    {
        Assert.Null(RunValidation.ValidateBranch(branch, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void A_branch_is_trimmed_and_kept_verbatim()
    {
        Assert.Null(RunValidation.ValidateBranch("  release/1.0  ", out var normalized));
        Assert.Equal("release/1.0", normalized);
    }

    [Theory]
    [InlineData("-leading-dash")]
    [InlineData("/leading-slash")]
    [InlineData("has space")]
    [InlineData("has..dots")]
    [InlineData("nested//slash")]
    [InlineData("trailing/")]
    [InlineData("colon:inside")]
    [InlineData("question?mark")]
    public void An_invalid_branch_is_a_field_error(string branch)
    {
        Assert.Equal(RunErrorCodes.BranchInvalid, RunValidation.ValidateBranch(branch, out _));
    }

    [Fact]
    public void A_branch_of_exactly_the_maximum_length_is_accepted_and_one_character_more_is_not()
    {
        // The run's snapshot goes into a character varying(200) column: the boundary the run form must
        // enforce is the project's, so a value the project accepts is a value the run can store.
        var boundary = new string('a', Zenkins.Server.Projects.ProjectValidation.MaxDefaultBranchLength);
        Assert.Null(RunValidation.ValidateBranch(boundary, out var normalized));
        Assert.Equal(boundary, normalized);

        var overLong = boundary + "a";
        Assert.Equal(RunErrorCodes.BranchInvalid, RunValidation.ValidateBranch(overLong, out _));
    }

    [Fact]
    public void A_branch_with_a_control_character_is_a_field_error()
    {
        Assert.Equal(
            RunErrorCodes.BranchInvalid,
            RunValidation.ValidateBranch("main\u0007next", out _));
    }

    [Fact]
    public void Required_tags_are_trimmed_deduplicated_and_case_insensitive()
    {
        Assert.Equal(["linux", "dotnet"], RunValidation.ParseTags(" linux , Linux,, dotnet "));
        Assert.Equal([], RunValidation.ParseTags(null));
        Assert.Equal([], RunValidation.ParseTags(" , , "));
    }

    [Fact]
    public void A_required_tag_over_64_characters_is_rejected_instead_of_dropped()
    {
        // A single over-long tag used to be dropped silently, normalizing to "no required tags" -
        // a run any worker could claim. It is a field error now (review fix).
        var overLong = new string('x', RunValidation.MaxTagLength + 1);

        var error = RunValidation.ValidateRequiredTags($"linux, {overLong}", out var requiredTags);

        Assert.Equal(RunErrorCodes.TagInvalid, error);
        Assert.Equal(string.Empty, requiredTags);
    }

    [Fact]
    public void A_required_tag_with_a_surviving_control_character_is_rejected_instead_of_dropped()
    {
        // The bell is a non-whitespace control character, so trimming cannot remove it and
        // ParseTags would silently drop the whole entry.
        var error = RunValidation.ValidateRequiredTags("linux,\u0007dotnet", out _);

        Assert.Equal(RunErrorCodes.TagInvalid, error);
    }

    [Fact]
    public void Required_tags_normalization_stays_silent_and_the_total_length_rule_holds()
    {
        // Trim, empty-entry drops and dedup stay silent; exactly 64 characters per tag is legal.
        Assert.Null(RunValidation.ValidateRequiredTags(" linux , Linux,, dotnet ", out var tags));
        Assert.Equal("linux,dotnet", tags);
        Assert.Null(
            RunValidation.ValidateRequiredTags(new string('x', RunValidation.MaxTagLength), out var boundary));
        Assert.Equal(new string('x', RunValidation.MaxTagLength), boundary);

        // The joined list's >400 rule is unchanged: 10 distinct 50-char tags + 9 commas = 509
        // (the tags must differ - dedup would shrink a repeated tag back under the limit).
        var tenDistinctTagsOfFifty = string.Join(
            ',',
            Enumerable.Range(0, 10).Select(i => $"{i:D2}{new string('a', 48)}"));
        Assert.Equal(
            RunErrorCodes.TagInvalid,
            RunValidation.ValidateRequiredTags(tenDistinctTagsOfFifty, out _));
    }

    [Fact]
    public void Worker_tags_cover_required_tags_case_insensitively()
    {
        Assert.True(RunValidation.WorkerTagsCover("linux, dotnet", "LINUX, DOTNET"));
        Assert.True(RunValidation.WorkerTagsCover("linux, LINUX", "linux"));
        Assert.True(RunValidation.WorkerTagsCover("", "anything"));
        Assert.True(RunValidation.WorkerTagsCover("", ""));
        Assert.False(RunValidation.WorkerTagsCover("linux", "windows, macos"));
        Assert.False(RunValidation.WorkerTagsCover("linux, dotnet", "linux")); // dotnet uncovered
        Assert.False(RunValidation.WorkerTagsCover("linux", ""));
    }
}

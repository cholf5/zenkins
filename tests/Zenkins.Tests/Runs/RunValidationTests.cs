using Xunit;
using Zenkins.Server.Runs;

namespace Zenkins.Tests.Runs;

/// <summary>
/// The pure trigger-form rules (card F-006): one argument per line, no shell quoting
/// interpretation, the card's argv limits, and tag parsing/covering.
/// </summary>
[Trait("Category", "Runs")]
public sealed class RunValidationTests
{
    [Fact]
    public void Command_lines_are_trimmed_and_blank_lines_are_dropped()
    {
        var argv = RunValidation.ParseArguments("  dotnet \r\n\r\n   --version  \n");

        Assert.Equal(["dotnet", "--version"], argv);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \r\n ")]
    public void An_empty_command_is_rejected(string? command)
    {
        var error = RunValidation.ValidateArguments(command, out var argv);

        Assert.Equal(RunErrorCodes.ArgvEmpty, error);
        Assert.Empty(argv);
    }

    [Fact]
    public void Exactly_100_arguments_are_accepted_and_101_are_rejected()
    {
        var ok = string.Join('\n', Enumerable.Repeat("arg", RunValidation.MaxArguments));
        Assert.Null(RunValidation.ValidateArguments(ok, out var argv));
        Assert.Equal(RunValidation.MaxArguments, argv.Length);

        var tooMany = ok + "\narg";
        Assert.Equal(
            RunErrorCodes.ArgvTooManyArguments,
            RunValidation.ValidateArguments(tooMany, out _));
    }

    [Fact]
    public void An_argument_of_exactly_32_kib_is_accepted_and_one_more_character_is_rejected()
    {
        var boundary = new string('a', RunValidation.MaxArgumentLength);
        Assert.Null(RunValidation.ValidateArguments(boundary, out var argv));
        Assert.Single(argv);

        var over = boundary + "a";
        Assert.Equal(
            RunErrorCodes.ArgvArgumentTooLong,
            RunValidation.ValidateArguments(over, out _));
    }

    [Fact]
    public void Quotes_are_literal_characters_of_an_argument()
    {
        // No shell interpretation anywhere: quotes survive into the argv verbatim.
        var error = RunValidation.ValidateArguments("echo \"hello world\"\n--flag=a;b|c", out var argv);

        Assert.Null(error);
        Assert.Equal(["echo \"hello world\"", "--flag=a;b|c"], argv);
    }

    [Fact]
    public void Command_json_round_trips_unicode_and_quotes()
    {
        var argv = new[] { "echo", "he said \"hi\"", "日本語", string.Empty };

        var json = RunValidation.EncodeCommandJson(argv);

        Assert.Equal(argv, RunValidation.DecodeCommandJson(json));
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

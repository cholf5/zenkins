using Xunit;
using Zenkins.Tests.Localization;
using Zenkins.Server.Projects;

namespace Zenkins.Tests.Projects;

/// <summary>
/// Card F-003's URL and path validators. These are pure functions so the hostile transports and
/// path shapes are pinned without a database or a host.
/// </summary>
[Trait("Category", "Projects")]
public sealed class ProjectValidationTests
{
    [Theory]
    [InlineData("https://github.com/acme/game-client.git")]
    [InlineData("https://git.example.test:8443/team/repo")]
    [InlineData("ssh://git@github.com/acme/game-client.git")]
    [InlineData("ssh://git@git.example.test:2222/team/repo.git")]
    [InlineData("git@github.com:acme/game-client.git")]
    [InlineData("git@git.example.test:team/repo")]
    public void Supported_repository_urls_pass(string url)
    {
        Assert.Null(ProjectValidation.ValidateRepositoryUrl(url, out var normalized));
        Assert.Equal(url, normalized);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://C:/Windows/win.ini")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("ext::sh")]
    [InlineData("fd::17")]
    [InlineData("git://github.com/acme/repo.git")]
    [InlineData("http://github.com/acme/repo.git")]
    [InlineData("ftp://example.test/repo.git")]
    [InlineData("ssh+git://git@example.test/repo")]
    [InlineData("--upload-pack=/tmp/pwned")]
    public void Dangerous_transports_are_rejected(string url) =>
        Assert.Equal(ProjectErrorCodes.RepositoryUrlInvalid, ProjectValidation.ValidateRepositoryUrl(url, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_repository_url_is_required(string url) =>
        Assert.Equal(ProjectErrorCodes.RepositoryUrlRequired, ProjectValidation.ValidateRepositoryUrl(url, out _));

    [Fact]
    public void Repository_url_is_trimmed_before_validation()
    {
        Assert.Null(ProjectValidation.ValidateRepositoryUrl("  https://example.test/repo.git  ", out var normalized));
        Assert.Equal("https://example.test/repo.git", normalized);
    }

    [Fact]
    public void Over_long_repository_url_is_rejected()
    {
        var url = "https://example.test/" + new string('a', ProjectValidation.MaxRepositoryUrlLength);
        Assert.Equal(ProjectErrorCodes.RepositoryUrlTooLong, ProjectValidation.ValidateRepositoryUrl(url, out _));
    }

    [Theory]
    [InlineData("pipeline.lua")]
    [InlineData("ci/pipeline.lua")]
    [InlineData("build/pipelines/release.lua")]
    public void Repository_relative_pipeline_paths_pass(string path)
    {
        Assert.Null(ProjectValidation.ValidatePipelinePath(path, out var normalized));
        Assert.Equal(path, normalized);
    }

    [Theory]
    [InlineData("../pipeline.lua")]
    [InlineData("ci/../../pipeline.lua")]
    [InlineData("/pipeline.lua")]
    [InlineData("\\pipeline.lua")]
    [InlineData("ci\\pipeline.lua")]
    [InlineData("C:/work/pipeline.lua")]
    [InlineData("C:\\work\\pipeline.lua")]
    [InlineData("ci//pipeline.lua")]
    [InlineData("ci/pipeline/")]
    public void Escaping_or_absolute_pipeline_paths_are_rejected(string path) =>
        Assert.Equal(ProjectErrorCodes.PipelinePathInvalid, ProjectValidation.ValidatePipelinePath(path, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_pipeline_path_defaults_to_pipeline_lua(string? path)
    {
        Assert.Null(ProjectValidation.ValidatePipelinePath(path, out var normalized));
        Assert.Equal(ProjectValidation.DefaultPipelinePath, normalized);
        Assert.Equal("pipeline.lua", normalized);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("release/2026.11")]
    [InlineData("feature-F003")]
    public void Normal_branch_names_pass(string branch)
    {
        Assert.Null(ProjectValidation.ValidateDefaultBranch(branch, out var normalized));
        Assert.Equal(branch, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../main")]
    [InlineData("/main")]
    [InlineData("-main")]
    [InlineData("main/../other")]
    [InlineData("main//next")]
    [InlineData("feature branch")]
    [InlineData("refs@{0}")]
    [InlineData("main.lock")]
    [InlineData("main.")]
    public void Invalid_branch_shapes_are_rejected(string branch)
    {
        var error = ProjectValidation.ValidateDefaultBranch(branch, out _);
        Assert.NotNull(error);
        Assert.Contains(
            error,
            new[] { ProjectErrorCodes.DefaultBranchRequired, ProjectErrorCodes.DefaultBranchInvalid });
    }

    [Fact]
    public void A_branch_of_exactly_the_maximum_length_passes_and_one_character_more_is_rejected()
    {
        // F-014 stores the branch in a character varying(200) column, so this boundary is what keeps a
        // run's snapshot storable. Both layers (project default, run branch) share the same rule.
        var boundary = new string('a', ProjectValidation.MaxDefaultBranchLength);
        Assert.Null(ProjectValidation.ValidateDefaultBranch(boundary, out var normalized));
        Assert.Equal(boundary, normalized);

        var overLong = boundary + "a";
        Assert.Equal(
            ProjectErrorCodes.DefaultBranchInvalid,
            ProjectValidation.ValidateDefaultBranch(overLong, out _));
    }

    [Fact]
    public void A_branch_with_a_control_character_is_rejected()
    {
        // A control character would reach git argv and the stored column; it is refused at the boundary.
        Assert.Equal(
            ProjectErrorCodes.DefaultBranchInvalid,
            ProjectValidation.ValidateDefaultBranch("main\u0007next", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_project_name_is_required(string? name) =>
        Assert.Equal(ProjectErrorCodes.NameRequired, ProjectValidation.ValidateName(name, out _));

    [Fact]
    public void Project_name_is_trimmed_and_length_checked()
    {
        Assert.Null(ProjectValidation.ValidateName("  game-client  ", out var normalized));
        Assert.Equal("game-client", normalized);

        var tooLong = new string('a', ProjectValidation.MaxNameLength + 1);
        Assert.Equal(ProjectErrorCodes.NameTooLong, ProjectValidation.ValidateName(tooLong, out _));
    }

    [Fact]
    public void Every_validation_code_has_a_field_and_a_message()
    {
        string[] codes =
        [
            ProjectErrorCodes.NameRequired,
            ProjectErrorCodes.NameTooLong,
            ProjectErrorCodes.NameInvalid,
            ProjectErrorCodes.NameDuplicate,
            ProjectErrorCodes.RepositoryUrlRequired,
            ProjectErrorCodes.RepositoryUrlTooLong,
            ProjectErrorCodes.RepositoryUrlInvalid,
            ProjectErrorCodes.DefaultBranchRequired,
            ProjectErrorCodes.DefaultBranchInvalid,
            ProjectErrorCodes.PipelinePathTooLong,
            ProjectErrorCodes.PipelinePathInvalid,
        ];

        Assert.All(codes, code =>
        {
            Assert.NotNull(ProjectErrorCodes.FieldFor(code));
            Assert.False(string.IsNullOrWhiteSpace(ProjectErrorCodes.MessageFor(code, TestText.English)));
        });
    }
}

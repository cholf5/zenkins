using Xunit;
using Zenkins.Server.Secrets;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// TC-004 (card F-012): the name rules, every boundary the card names. A secret name must be usable
/// directly as an environment variable by F-015, so the validator is an allow-list
/// (<c>^[A-Za-z_][A-Za-z0-9_]{0,63}$</c>), not a deny-list.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void An_empty_name_is_required(string name)
    {
        Assert.Equal(SecretErrorCodes.NameRequired, SecretValidation.ValidateName(name, out _));
    }

    [Theory]
    [InlineData("1TOKEN")]
    [InlineData("2FA")]
    [InlineData("-TOKEN")]
    [InlineData("A-VAR")]
    [InlineData("A B")]
    [InlineData("A=B")]
    [InlineData("$(x)")]
    [InlineData("$TOKEN")]
    [InlineData("TOKEN!")]
    [InlineData("TOKEN\nNEXT")]
    [InlineData("NUGET.TOKEN")]
    public void A_name_that_is_not_environment_variable_shaped_is_invalid(string name)
    {
        Assert.Equal(SecretErrorCodes.NameInvalid, SecretValidation.ValidateName(name, out _));
    }

    [Fact]
    public void A_name_longer_than_64_characters_is_too_long()
    {
        var name = "A" + new string('B', SecretValidation.MaxNameLength - 1 + 1);

        Assert.Equal(SecretErrorCodes.NameTooLong, SecretValidation.ValidateName(name, out _));
    }

    [Theory]
    [InlineData("NUGET_TOKEN")]
    [InlineData("_private")]
    [InlineData("A")]
    [InlineData("lower_case_and_123")]
    public void An_environment_variable_shaped_name_is_accepted(string name)
    {
        Assert.Null(SecretValidation.ValidateName(name, out var normalized));
        Assert.Equal(name, normalized);
    }

    [Fact]
    public void A_64_character_name_is_the_longest_accepted_name()
    {
        var name = "A" + new string('B', SecretValidation.MaxNameLength - 1);

        Assert.Equal(SecretValidation.MaxNameLength, name.Length);
        Assert.Null(SecretValidation.ValidateName(name, out var normalized));
        Assert.Equal(name, normalized);
    }

    [Fact]
    public void A_name_is_trimmed_to_its_canonical_form()
    {
        Assert.Null(SecretValidation.ValidateName("  NUGET_TOKEN  ", out var normalized));
        Assert.Equal("NUGET_TOKEN", normalized);
    }

    [Fact]
    public void An_empty_value_is_required()
    {
        Assert.Equal(SecretErrorCodes.ValueRequired, SecretValidation.ValidateValue(string.Empty, out _));
        Assert.Equal(SecretErrorCodes.ValueRequired, SecretValidation.ValidateValue(null, out _));
    }

    [Fact]
    public void An_over_long_value_is_rejected()
    {
        var value = new string('x', SecretValidation.MaxValueLength + 1);

        Assert.Equal(SecretErrorCodes.ValueTooLong, SecretValidation.ValidateValue(value, out _));
    }

    [Fact]
    public void A_value_is_opaque_and_never_trimmed()
    {
        // A private key block legitimately contains newlines and trailing whitespace; normalising a
        // value would corrupt it.
        const string Value = "  line-one\nline-two\n  ";

        Assert.Null(SecretValidation.ValidateValue(Value, out var normalized));
        Assert.Equal(Value, normalized);
    }

    [Fact]
    public void Every_error_code_maps_to_the_field_and_a_message()
    {
        string[] codes =
        [
            SecretErrorCodes.NameRequired,
            SecretErrorCodes.NameTooLong,
            SecretErrorCodes.NameInvalid,
            SecretErrorCodes.NameDuplicate,
            SecretErrorCodes.ValueRequired,
            SecretErrorCodes.ValueTooLong,
        ];

        foreach (var code in codes)
        {
            Assert.NotNull(SecretErrorCodes.FieldFor(code));
            Assert.False(string.IsNullOrWhiteSpace(SecretErrorCodes.MessageFor(code)));
        }

        Assert.Equal(SecretFields.Name, SecretErrorCodes.FieldFor(SecretErrorCodes.NameDuplicate));
        Assert.Equal(SecretFields.Value, SecretErrorCodes.FieldFor(SecretErrorCodes.ValueTooLong));
    }
}

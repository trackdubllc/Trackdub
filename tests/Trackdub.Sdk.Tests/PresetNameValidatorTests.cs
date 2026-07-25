using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class PresetNameValidatorTests
{
    [Theory]
    [InlineData("my-preset")]
    [InlineData("test_123")]
    [InlineData("a")]
    [InlineData("ABC")]
    [InlineData("mix-of_Everything123")]
    public void IsValid_ValidNames_ReturnsTrue(string name)
    {
        Assert.True(PresetNameValidator.IsValid(name));
    }

    [Fact]
    public void IsValid_MaxLength64_ReturnsTrue()
    {
        string name = new('a', 64);
        Assert.True(PresetNameValidator.IsValid(name));
    }

    [Fact]
    public void IsValid_ExceedsMaxLength65_ReturnsFalse()
    {
        string name = new('a', 65);
        Assert.False(PresetNameValidator.IsValid(name));
    }

    [Fact]
    public void IsValid_Null_ReturnsFalse()
    {
        Assert.False(PresetNameValidator.IsValid(null));
    }

    [Fact]
    public void IsValid_Empty_ReturnsFalse()
    {
        Assert.False(PresetNameValidator.IsValid(string.Empty));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has.dot")]
    [InlineData("special!char")]
    [InlineData("at@sign")]
    [InlineData("hash#tag")]
    [InlineData("pct%val")]
    [InlineData("dollar$")]
    [InlineData("path/slash")]
    [InlineData("back\\slash")]
    public void IsValid_InvalidCharacters_ReturnsFalse(string name)
    {
        Assert.False(PresetNameValidator.IsValid(name));
    }
}

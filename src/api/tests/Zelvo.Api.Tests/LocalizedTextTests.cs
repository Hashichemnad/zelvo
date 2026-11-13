using Zelvo.Data.Localization;

namespace Zelvo.Api.Tests;

public sealed class LocalizedTextTests
{
    [Fact]
    public void Blank_values_count_as_missing_and_are_trimmed()
    {
        var text = new LocalizedText("  Incidents ", "   ");

        Assert.Equal("Incidents", text.En);
        Assert.Null(text.Ar);
        Assert.Equal(["ar"], text.MissingLanguages);
        Assert.False(text.IsComplete);
        Assert.True(text.HasAnyValue);
    }

    [Fact]
    public void A_complete_value_has_nothing_missing()
    {
        var text = new LocalizedText("Incidents", "البلاغات");

        Assert.True(text.IsComplete);
        Assert.Empty(text.MissingLanguages);
        Assert.Equal("البلاغات", text.Get("ar"));
    }

    [Theory]
    [InlineData("en", "البلاغات")]
    [InlineData("ar", "البلاغات")]
    public void Falls_back_to_the_other_language(string language, string expected)
    {
        Assert.Equal(expected, new LocalizedText(null, "البلاغات").GetOrFallback(language));
    }

    [Fact]
    public void Unsupported_languages_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalizedText("a", "b").Get("fr"));
    }

    [Fact]
    public void Values_compare_by_content()
    {
        Assert.Equal(new LocalizedText("a", "ب"), new LocalizedText(" a", "ب "));
    }
}

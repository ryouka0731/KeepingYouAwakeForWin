using System.Globalization;
using System.Text.RegularExpressions;

namespace KeepingYouAwake.Core.Tests;

/// <summary>Every bundled language: completeness, placeholders, plural rules and culture resolution.</summary>
[Collection(StringsCollection.Name)]
public partial class LocalizationTests : IDisposable
{
    public void Dispose() => Strings.OverrideLanguage(null);

    public static TheoryData<string> Languages => new(Strings.Languages);

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string[] Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void AllExpectedLanguagesAreBundled() =>
        Assert.Equal(
            ["da", "de", "el", "en", "es", "fi", "fr", "hi", "id", "it", "ja", "ko", "nl", "pl", "pt", "ru", "sk", "tr", "uk", "vi", "zh", "zh-Hant"],
            Strings.Languages);

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryKeyIsTranslatedWithItsPlaceholders(string language)
    {
        foreach (var key in Strings.Keys)
        {
            if (key.StartsWith("Unit.", StringComparison.Ordinal) && key.EndsWith(".one", StringComparison.Ordinal) && !Strings.Has(key, language))
            {
                continue; // languages without a "one" form use "other"
            }
            Assert.True(Strings.Has(key, language), $"{language}: missing {key}");
            var text = Strings.Get(key, language);
            if (key is not ("Unit.separator")) { Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: empty {key}"); }
            Assert.Equal(Placeholders(Strings.Get(key, "en")), Placeholders(text));
        }
        Assert.EndsWith("|*.exe", Strings.Get("ApplicationFilter", language));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryPluralCategoryTheRulesUseHasText(string language)
    {
        var categories = Enumerable.Range(0, 200).Select(n => Strings.PluralCategory(language, n)).Distinct();
        foreach (var unit in new[] { "hour", "minute", "second" })
        {
            foreach (var category in categories)
            {
                Assert.True(Strings.Has($"Unit.{unit}.{category}", language) || Strings.Has($"Unit.{unit}.other", language),
                    $"{language}: no Unit.{unit}.{category}");
            }
        }
    }

    [Theory]
    [InlineData("ru", 1, "one")]
    [InlineData("ru", 2, "few")]
    [InlineData("ru", 5, "many")]
    [InlineData("ru", 11, "many")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 22, "few")]
    [InlineData("ru", 112, "many")]
    [InlineData("pl", 1, "one")]
    [InlineData("pl", 21, "many")]
    [InlineData("pl", 24, "few")]
    [InlineData("sk", 3, "few")]
    [InlineData("sk", 5, "many")]
    [InlineData("fr", 0, "one")]
    [InlineData("fr", 2, "other")]
    [InlineData("en", 0, "other")]
    [InlineData("ja", 1, "other")]
    public void PluralCategories(string language, long n, string expected) =>
        Assert.Equal(expected, Strings.PluralCategory(language, n));

    [Fact]
    public void RussianDurationsUseThreePluralForms()
    {
        Strings.OverrideLanguage("ru");
        var one = Strings.DurationText(TimeSpan.FromHours(1));
        var few = Strings.DurationText(TimeSpan.FromHours(2));
        var many = Strings.DurationText(TimeSpan.FromHours(5));
        Assert.Equal(3, new[] { one, few, many }.Select(t => t.Split(' ')[1]).Distinct().Count());
        Assert.Equal(one.Split(' ')[1], Strings.DurationText(TimeSpan.FromHours(21)).Split(' ')[1]);
    }

    [Theory]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-Hans-SG", "zh")]
    [InlineData("pt-BR", "pt")]
    [InlineData("de-AT", "de")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-GB", "en")]
    [InlineData("sv-SE", "en")] // not bundled: English
    [InlineData("", "en")]
    public void CulturesResolveToBundledLanguages(string culture, string expected) =>
        Assert.Equal(expected, Strings.ResolveLanguage(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void OverrideFollowsBundledLanguagesOnly()
    {
        Strings.OverrideLanguage("de");
        Assert.Equal("de", Strings.Language);
        Strings.OverrideLanguage("xx");
        Assert.Equal(Strings.ResolveLanguage(CultureInfo.CurrentUICulture), Strings.Language);
        Assert.Equal("missing.key", Strings.Get("missing.key", "de"));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void WeekdayNamesComeFromTheLanguage(string language)
    {
        Strings.OverrideLanguage(language);
        var names = Enum.GetValues<DayOfWeek>().Select(Strings.WeekdayName).ToList();
        Assert.Equal(7, names.Distinct().Count());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        var window = new ScheduleWindow { Weekdays = [DayOfWeek.Monday, DayOfWeek.Friday], StartMinutes = 540, EndMinutes = 1020 };
        Assert.Contains(names[(int)DayOfWeek.Friday], TrayPresenter.DescribeScheduleWindow(window));
    }

    [Fact]
    public void GermanTitlesAreGerman()
    {
        Strings.OverrideLanguage("de");
        Assert.NotEqual(Strings.Get("Quit", "en"), Strings.Get("Quit"));
        Assert.Equal("Mo", Strings.WeekdayName(DayOfWeek.Monday));
    }
}

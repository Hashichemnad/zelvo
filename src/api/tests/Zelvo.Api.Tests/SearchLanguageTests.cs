using Microsoft.Extensions.DependencyInjection;
using Zelvo.Search;

namespace Zelvo.Api.Tests;

/// <summary>
/// LANG-9: search works across English and Arabic, matching Arabic with or without diacritics and
/// with different letter forms (أ/ا/إ/آ, ة/ه, ى/ي and others).
/// </summary>
[Collection(SearchTestGroup.Name)]
public sealed class SearchLanguageTests(DatabaseFixture database, SearchFixture search)
{
    private const string Password = "password";
    private const string Leave = "leave";
    private const string Printer = "printer";
    private const string Welcome = "welcome";
    private const string Hospital = "hospital";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Spelling variants that must analyse to the same tokens, in Arabic and in text of unknown language.
    [Theory]
    [InlineData("إعادة", "اعادة")]           // hamza below alef / bare alef
    [InlineData("أعادة", "اعادة")]           // hamza above alef / bare alef
    [InlineData("آمن", "امن")]               // madda / bare alef
    [InlineData("ٱلمرور", "المرور")]         // alef wasla
    [InlineData("كلمة", "كلمه")]             // teh marbuta / heh
    [InlineData("مستشفى", "مستشفي")]         // alef maqsura / yeh
    [InlineData("إِعَادَةُ", "اعادة")]       // full diacritics
    [InlineData("كَلِمَةِ ٱلْمُرُورِ", "كلمه المرور")]
    [InlineData("الطّابعة", "الطابعه")]      // shadda
    [InlineData("كـــلمة", "كلمة")]          // tatweel
    [InlineData("مسؤول", "مسوول")]           // hamza on waw
    [InlineData("ﻻﻋﺐ", "لاعب")]              // presentation forms (lam-alef ligature)
    [InlineData("ی", "ي")]                   // Farsi yeh
    [InlineData("ک", "ك")]                   // keheh
    [InlineData("رقم ١٢٣", "رقم 123")]       // Arabic-Indic digits
    public async Task Arabic_spelling_variants_are_analysed_the_same(string variant, string plain)
    {
        var index = await AnalysisIndexAsync();

        foreach (var analyzer in new[] { SearchIndexDefinition.ArabicAnalyzer, SearchIndexDefinition.FoldedAnalyzer })
        {
            var expected = await search.Client.AnalyzeAsync(index, analyzer, plain, Ct);
            var actual = await search.Client.AnalyzeAsync(index, analyzer, variant, Ct);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("café résumé", "cafe resume")]  // accents
    [InlineData("printers", "printer")]         // plural
    [InlineData("printer's", "printer")]        // possessive
    [InlineData("PRINTER", "printer")]          // case
    public async Task English_variants_are_analysed_the_same(string variant, string plain)
    {
        var index = await AnalysisIndexAsync();

        var expected = await search.Client.AnalyzeAsync(index, SearchIndexDefinition.EnglishAnalyzer, plain, Ct);
        var actual = await search.Client.AnalyzeAsync(index, SearchIndexDefinition.EnglishAnalyzer, variant, Ct);

        Assert.Equal(expected, actual);
    }

    [Theory]
    // Without diacritics, and with different letter forms than the stored text.
    [InlineData("اعادة تعيين كلمة المرور", Password)]   // إ → ا
    [InlineData("أعادة تعيين كلمه المرور", Password)]   // إ → أ, ة → ه
    [InlineData("اعاده تعيين كلمه المرور", Password)]   // no hamza, heh for teh marbuta
    [InlineData("اجازه سنويه", Leave)]                  // إ → ا, ة → ه
    [InlineData("إجازة", Leave)]
    [InlineData("مستشفي", Hospital)]                    // ى → ي
    [InlineData("المستشفى", Hospital)]
    // With full diacritics, against stored text without them.
    [InlineData("إِعَادَةُ تَعْيِينِ كَلِمَةِ ٱلْمُرُورِ", Password)]
    [InlineData("إِجَازَةٌ سَنَوِيَّةٌ", Leave)]
    // Without diacritics, against stored text with them.
    [InlineData("مرحبا بكم في البوابة", Welcome)]
    [InlineData("البوابه", Welcome)]
    // Word forms: article, prepositions, suffixes.
    [InlineData("طابعة", Printer)]
    [InlineData("بالطابعه", Printer)]
    // English, and both languages at once.
    [InlineData("reset password", Password)]
    [InlineData("printers", Printer)]
    [InlineData("Printer's", Printer)]
    [InlineData("password المرور", Password)]
    // Typing the start of a word (search as you type).
    [InlineData("passw", Password)]
    [InlineData("إجاز", Leave)]
    [InlineData("اجاز", Leave)]
    public async Task Searches_find_the_document_whatever_the_spelling(string query, string expectedId)
    {
        var tenant = await TenantWithArticlesAsync();

        var results = await search.SearchAsync(tenant, query);

        Assert.NotEmpty(results.Items);
        Assert.Equal(expectedId, results.Items[0].Id);
    }

    [Fact]
    public async Task Results_are_highlighted_in_both_languages_and_escaped()
    {
        var tenant = await TenantWithArticlesAsync();

        var results = await search.SearchAsync(tenant, "اعادة تعيين password");
        var hit = Assert.Single(results.Items, h => h.Id == Password);

        Assert.Contains("<mark>", hit.Highlights["title.ar"][0], StringComparison.Ordinal);
        Assert.Contains("<mark>", hit.Highlights["title.en"][0], StringComparison.Ordinal);
        Assert.Equal("Reset your password", hit.Title.En);
        Assert.Equal("إعادة تعيين كلمة المرور", hit.Title.Ar);
        Assert.Equal("/knowledge/password", hit.Url);
        Assert.Contains("&lt;b&gt;", string.Join(' ', hit.Highlights["body.en"]), StringComparison.Ordinal);
    }

    private Task<string> AnalysisIndexAsync() => TenantIndexAsync();

    private async Task<string> TenantIndexAsync()
    {
        var tenant = await database.CreateTenantAsync("lang");
        await search.Pipeline.GetRequiredService<SearchIndexManager>().EnsureAsync(tenant.Id, Ct);
        return search.AliasFor(tenant);
    }

    private async Task<Data.Tenancy.Tenant> TenantWithArticlesAsync()
    {
        var tenant = await database.CreateTenantAsync("lang");
        await search.IndexAsync(tenant,
            TestDocuments.Article(Password, "Reset your password", "إعادة تعيين كلمة المرور",
                "Use the <b>forgot password</b> link on the sign-in page.", "استخدم رابط نسيت كلمة المرور في صفحة الدخول."),
            TestDocuments.Article(Leave, "Annual leave request", "طلب إجازة سنوية"),
            TestDocuments.Article(Printer, "Printer is not working", "الطابعة لا تعمل"),
            TestDocuments.Article(Welcome, "Welcome to the portal", "مَرْحَبًا بِكُمْ فِي الْبَوَّابَةِ"),
            TestDocuments.Article(Hospital, "Visiting hours at the hospital", "مواعيد الزيارة في المستشفى"));
        await search.WaitUntilIndexedAsync(tenant);
        return tenant;
    }
}

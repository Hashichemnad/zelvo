using Zelvo.Data;
using Zelvo.Data.Branding;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task F1: each sample organisation runs under its own product name, brand colours and font
/// (published, so the demo shows white-labelling at once); logos, custom domains and the email
/// sender domain are left for the demo's administrator to try.
/// </summary>
internal sealed class DemoBrandingSeeder : IDemoSeeder
{
    public string Key => "f1-branding";

    public int Order => 220;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var value = government
            ? new BrandingValue { ProductName = new LocalizedText("Municipality e-Services", "الخدمات الإلكترونية للبلدية"), LightColour = "#00684a", DarkColour = "#6fd3a8", Font = "tajawal" }
            : new BrandingValue { ProductName = new LocalizedText("Company IT Help", "مساعدة تقنية الشركة"), LightColour = "#5b2a86", DarkColour = "#c9a8f0", Font = "ibm-plex-sans-arabic" };
        var json = value.Write();
        db.TenantBrandings.Add(new TenantBranding { Draft = json, Published = json, PublishedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

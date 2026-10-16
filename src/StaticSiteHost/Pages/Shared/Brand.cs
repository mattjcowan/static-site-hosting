namespace StaticSiteHost.Pages;

/// <summary>
/// The name the management app goes by when its branding sets none (see BrandingService.ViewAsync).
/// Not text to translate: it is the product's name, and branding replaces it. The pages' own words
/// never name the product (see the ui-copy skill).
/// </summary>
public static class Brand
{
    public const string DefaultName = "Static Site Host";
}

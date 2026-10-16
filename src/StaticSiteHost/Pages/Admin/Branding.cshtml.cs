using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Pages.Shared;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Admin;

/// <summary>
/// The server's branding: the name, logos, colours, fonts and sizes of the management app and of
/// every site's visitor screens, and the font library. Administrators only, via the /Admin folder
/// policy. A site's own branding for its visitor screens is on its page, under Settings.
/// </summary>
public class BrandingModel : PageModel
{
    private readonly BrandingService _branding;
    private readonly IStringLocalizer<Strings> _strings;

    public BrandingModel(BrandingService branding, IStringLocalizer<Strings> strings)
    {
        _branding = branding;
        _strings = strings;
    }

    public BrandingSettings Settings { get; private set; } = new();

    public BrandingEditor Editor { get; private set; } = null!;

    private string Actor => User.Identity?.Name ?? "unknown";

    public async Task OnGetAsync() => await LoadAsync([]);

    private async Task LoadAsync(IReadOnlyList<string> errors, Branding? shown = null)
    {
        Settings = await _branding.Store.ReadAsync(HttpContext.RequestAborted);
        var saved = Settings.Server;
        Editor = new BrandingEditor(
            BrandingEditor.Filled(shown ?? saved, null), shown ?? saved, Settings.Fonts, Settings.AllowGoogleFonts,
            Site: null, Inherited: null, errors, Url.Page("/Admin/Branding", "Save")!);
    }

    public async Task<IActionResult> OnPostSaveAsync(BrandingInput input)
    {
        var settings = await _branding.Store.ReadAsync(HttpContext.RequestAborted);
        var (branding, errors) = await input.ToBrandingAsync(settings.Server, null, _branding.Store, _strings, HttpContext.RequestAborted);
        if (errors.Count == 0) errors = await _branding.SetServerAsync(branding, Actor, HttpContext.RequestAborted);

        if (errors.Count > 0)
        {
            await LoadAsync(errors, branding);
            return Page();
        }

        TempData["StatusMessage"] = _strings.Text("branding.form.saved");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGoogleAsync(bool allow)
    {
        await _branding.SetAllowGoogleFontsAsync(allow, Actor, HttpContext.RequestAborted);
        TempData["StatusMessage"] = _strings.Text(allow ? "branding.google.allowed" : "branding.google.disallowed");
        return RedirectToPage(null, null, null, "fonts");
    }

    public async Task<IActionResult> OnPostAddGoogleFontAsync(string? family, string? mode)
    {
        var (font, error) = await _branding.AddGoogleFontAsync(family ?? "", mode != "link", Actor, HttpContext.RequestAborted);
        return Added(font, error);
    }

    public async Task<IActionResult> OnPostUploadFontAsync(string? family, List<IFormFile>? files)
    {
        var uploads = new List<(string FileName, byte[] Data)>();
        foreach (var file in (files ?? []).Where(f => f.Length > 0))
        {
            if (file.Length > BrandingStore.MaxFontFileBytes)
            {
                TempData["ErrorMessage"] = _strings.Text("branding.fonts.upload.tooLarge", new { file = file.FileName, size = BrandingStore.MaxFontFileBytes / (1024 * 1024) });
                return RedirectToPage(null, null, null, "fonts");
            }
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, HttpContext.RequestAborted);
            uploads.Add((file.FileName, buffer.ToArray()));
        }

        if (uploads.Count == 0)
        {
            TempData["ErrorMessage"] = _strings.Text("branding.fonts.upload.noFiles");
            return RedirectToPage(null, null, null, "fonts");
        }

        var (font, error) = await _branding.AddUploadedFontAsync(family ?? "", uploads, Actor, HttpContext.RequestAborted);
        return Added(font, error);
    }

    private IActionResult Added(BrandFont? font, string? error)
    {
        if (font is null) TempData["ErrorMessage"] = error;
        else if (font.Kind == BrandFont.GoogleLinked) TempData["StatusMessage"] = _strings.Text("branding.fonts.addedLinked", new { family = font.Family });
        else TempData["StatusMessage"] = _strings.Text("branding.fonts.added",
            new { family = font.Family, files = _strings.Plural("branding.fonts.files", font.Files.Count) });
        return RedirectToPage(null, null, null, "fonts");
    }

    public async Task<IActionResult> OnPostRemoveFontAsync(string? id)
    {
        var settings = await _branding.Store.ReadAsync(HttpContext.RequestAborted);
        var font = settings.Fonts.FirstOrDefault(f => f.Id == id);
        if (font is not null && await _branding.RemoveFontAsync(font.Id, Actor, HttpContext.RequestAborted))
            TempData["StatusMessage"] = _strings.Text("branding.fonts.removed", new { family = font.Family });
        else
            TempData["ErrorMessage"] = _strings.Text("branding.fonts.notFound");
        return RedirectToPage(null, null, null, "fonts");
    }
}

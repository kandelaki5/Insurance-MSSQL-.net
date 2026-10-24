using System.Text.RegularExpressions;
using Insurance.Application.Texts;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Insurance.Web.Infrastructure;

/// <summary>
/// &lt;date-input name="…" value="yyyy-mm-dd" min max required disabled id&gt;: a date typed as dd/mm/yyyy whose form value is
/// the ISO date (legacy DateInput). The behaviour is wwwroot/js/date-input.js.
/// </summary>
[HtmlTargetElement("date-input", TagStructure = TagStructure.WithoutEndTag)]
public sealed partial class DateInputTagHelper : TagHelper
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
    public string? Min { get; set; }
    public string? Max { get; set; }
    public string? Id { get; set; }
    public bool Required { get; set; }
    public bool Disabled { get; set; }
    public string? Style { get; set; }

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})$")]
    private static partial Regex Iso();

    public static string IsoToText(string? iso) => iso is not null && Iso().Match(iso) is { Success: true } m ? $"{m.Groups[3]}/{m.Groups[2]}/{m.Groups[1]}" : "";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var text = IsoToText(Value);
        var iso = text == "" ? "" : Value!;
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "date-input");
        if (Style is not null) output.Attributes.SetAttribute("style", Style);
        if (Min is not null) output.Attributes.SetAttribute("data-min", Min);
        if (Max is not null) output.Attributes.SetAttribute("data-max", Max);
        output.Attributes.SetAttribute("data-invalid", Ka.Common.DateInvalid);
        output.Attributes.SetAttribute("data-early", Ka.Common.DateTooEarly("{0}"));
        output.Attributes.SetAttribute("data-late", Ka.Common.DateTooLate("{0}"));
        Func<string?, string> e = s => System.Net.WebUtility.HtmlEncode(s ?? "");
        var disabled = Disabled ? " disabled" : "";
        output.Content.SetHtmlContent(
            $"<input{(Id is null ? "" : $" id=\"{e(Id)}\"")} type=\"text\" inputmode=\"numeric\" autocomplete=\"off\" placeholder=\"{e(Ka.Common.DateFormat)}\" maxlength=\"10\" value=\"{e(text)}\"{(Required ? " required" : "")}{disabled} />" +
            $"<input type=\"hidden\" name=\"{e(Name)}\" value=\"{e(iso)}\"{disabled} />" +
            $"<input type=\"date\" class=\"date-input-picker\" tabindex=\"-1\" aria-hidden=\"true\"{(Min is null ? "" : $" min=\"{e(Min)}\"")}{(Max is null ? "" : $" max=\"{e(Max)}\"")} value=\"{e(iso)}\"{disabled} />" +
            $"<button type=\"button\" class=\"date-input-button\" title=\"{e(Ka.Common.OpenCalendar)}\" aria-label=\"{e(Ka.Common.OpenCalendar)}\"{disabled}>" +
            "<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" aria-hidden=\"true\"><rect x=\"1.5\" y=\"3\" width=\"13\" height=\"11.5\" rx=\"1.5\" fill=\"none\" stroke=\"currentColor\" />" +
            "<path d=\"M1.5 6.5h13M5 1.5v3M11 1.5v3\" stroke=\"currentColor\" fill=\"none\" /></svg></button>");
    }
}

namespace PhoneBox.Agent;

/// <summary>Optional appearance overrides. Null means inherit; the default preset restores the original renderer.</summary>
public sealed class AppearanceConfig
{
    public string? Style { get; set; }
    public string? Font { get; set; }
    public int? FontScale { get; set; }
    public int? Radius { get; set; }
    public int? Gap { get; set; }
    public int? Padding { get; set; }
    public int? PagePadding { get; set; }
    public bool? Border { get; set; }
    public bool? Shadow { get; set; }

    internal void Normalize(bool widget = false)
    {
        Style = Style is "default" or "cards" or "flat" or "borderless" or "seamless" ? Style : null;
        Font = Font is "default" or "sans" or "rounded" or "condensed" or "serif" or "mono" ? Font : null;
        FontScale = FontScale is { } s ? Math.Clamp(s, 50, 200) : null;
        Radius = Bound(Radius, 32);
        Gap = widget ? null : Bound(Gap, 24);
        Padding = Bound(Padding, 24);
        PagePadding = widget ? null : Bound(PagePadding, 32);
    }
    private static int? Bound(int? value, int max) => value is { } n ? Math.Clamp(n, 0, max) : null;
    public AppearanceConfig Clone() => (AppearanceConfig)MemberwiseClone();
}

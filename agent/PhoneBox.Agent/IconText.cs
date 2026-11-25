using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneBox.Agent;

/// <summary>Only repairs exact known ANSI-decoded forms of the affected default icons.</summary>
internal static class IconText
{
    private static readonly Dictionary<string, string> Repairs = BuildRepairs();
    public static string Repair(string? value) => value is not null && Repairs.TryGetValue(value, out var icon) ? icon : value ?? "";

    private static Dictionary<string, string> BuildRepairs()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Undefined Windows-1252 bytes keep their C1 values, as in Windows ANSI decoding.
        const string c1 = "\u20ac\u0081\u201a\u0192\u201e\u2026\u2020\u2021\u02c6\u2030\u0160\u2039\u0152\u008d\u017d\u008f\u0090\u2018\u2019\u201c\u201d\u2022\u2013\u2014\u02dc\u2122\u0161\u203a\u0153\u009d\u017e\u0178";
        foreach (var icon in new[] { "\u2328\ufe0f", "\u2699\ufe0f", "\u2702\ufe0f", "\u2733\ufe0f" })
        {
            var forms = new[] { icon, icon[..1] }.ToHashSet(StringComparer.Ordinal);
            for (var pass = 0; pass < 2; pass++)
            {
                var next = new HashSet<string>(StringComparer.Ordinal);
                foreach (var form in forms)
                {
                    var bytes = Encoding.UTF8.GetBytes(form);
                    next.Add(Encoding.Latin1.GetString(bytes));
                    next.Add(new string(bytes.Select(b => b is >= 0x80 and <= 0x9f ? c1[b - 0x80] : (char)b).ToArray()));
                }
                foreach (var broken in next) result[broken] = icon;
                forms = next;
            }
        }
        return result;
    }
}

/// <summary>Escape icon values even when surrounding user text is saved as readable UTF-8.</summary>
public sealed class IconJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonEncodedText.Encode(value, JavaScriptEncoder.Default));
}

/// <summary>A shortcut's icon size is a short preset name; any other JSON token (a number, an object) is dropped so a
/// hand-edited config.json still loads and the key just uses the default size.</summary>
public sealed class IconSizeJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        reader.Skip();
        return null;
    }
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

using System.Text.Json.Serialization;

namespace PhoneBox.Agent;

/// <summary>
/// The MQTT broker the agent connects to (Settings › General › MQTT). Off by default. The agent holds the connection:
/// the phone never gets the broker's address, the user name, the password or any topic.
/// </summary>
public sealed class MqttConfig
{
    public const string Tcp = "tcp";
    public const string WebSocket = "websocket";

    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 1883;
    /// <summary>"tcp" or "websocket".</summary>
    public string Transport { get; set; } = Tcp;
    /// <summary>The path of a WebSocket broker (usually /mqtt).</summary>
    public string WebSocketPath { get; set; } = "/mqtt";
    public bool Tls { get; set; }
    /// <summary>A broker with its own certificate is trusted through this CA file (PEM or DER)...</summary>
    public string? CaFile { get; set; }
    /// <summary>...or through the SHA-256 fingerprint of its certificate. There is no switch to skip the check.</summary>
    public string? PinnedFingerprint { get; set; }
    public string? ClientId { get; set; }
    public string? Username { get; set; }
    /// <summary>Write-only: Settings never gets it back (see <see cref="Redact"/>), and it is not in the status or the logs.</summary>
    public string? Password { get; set; }
    public int KeepAliveSec { get; set; } = 60;
    public bool CleanSession { get; set; } = true;
    /// <summary>The broker publishes this when the agent disappears without saying goodbye. Empty topic = no last will.</summary>
    public string? WillTopic { get; set; }
    public string? WillPayload { get; set; }
    public int WillQos { get; set; }
    /// <summary>Off by default: a retained message is delivered again whenever a device connects.</summary>
    public bool WillRetain { get; set; }

    [JsonIgnore] public bool Configured => Enabled && Host is { Length: > 0 };

    internal static MqttConfig Normalized(MqttConfig? c)
    {
        c ??= new();
        // trimmed and nothing else: a control character in the middle ("bro\nker") makes the address unusable, which is reported
        // when connecting ("config"), instead of being stripped into a host name the user never typed
        var host = c.Host?.Trim() ?? "";
        c.Host = host.Length == 0 ? null : host.Length > 253 ? host[..253] : host;
        c.Port = c.Port is >= 1 and <= 65535 ? c.Port : 1883;
        c.Transport = string.Equals(c.Transport?.Trim(), WebSocket, StringComparison.OrdinalIgnoreCase) ? WebSocket : Tcp;
        var path = c.WebSocketPath?.Trim() ?? "";
        if (path.Length == 0) path = "/mqtt";
        if (!path.StartsWith('/')) path = "/" + path;
        // the path goes into the connection address: no query, fragment, space or control character
        c.WebSocketPath = path.Length > 200 || path.Any(ch => char.IsControl(ch) || ch is ' ' or '?' or '#' or '\\') ? "/mqtt" : path;
        c.CaFile = Clean(c.CaFile, 1024);
        c.PinnedFingerprint = CleanFingerprint(c.PinnedFingerprint);
        c.ClientId = Clean(c.ClientId, 128);
        c.Username = Clean(c.Username, 256);
        // not trimmed: a password may begin or end with a space
        c.Password = string.IsNullOrEmpty(c.Password) ? null : c.Password.Length > 1024 ? c.Password[..1024] : c.Password;
        // an empty field arrives as 0: the default, not the shortest keep-alive
        c.KeepAliveSec = c.KeepAliveSec <= 0 ? 60 : Math.Clamp(c.KeepAliveSec, 5, 600);
        // a topic to publish to has no wildcard
        var will = Clean(c.WillTopic, 256);
        c.WillTopic = will is not null && !will.Any(ch => ch is '+' or '#' or '\0') ? will : null;
        // the message is sent as it is: no trimming, new lines stay
        c.WillPayload = c.WillTopic is null || string.IsNullOrEmpty(c.WillPayload) ? null : c.WillPayload.Length > 1024 ? c.WillPayload[..1024] : c.WillPayload;
        c.WillQos = Math.Clamp(c.WillQos, 0, 2);
        if (c.WillTopic is null) c.WillRetain = false;
        return c;
    }

    /// <summary>
    /// The broker's address, or null for anything that is not a bare host name or IP address (a pasted mqtt:// address, a
    /// path, a user name): it goes into a connection address, so nothing else may ride along. IPv6 may be in brackets.
    /// </summary>
    internal static string? CleanHost(string? host)
    {
        var h = host?.Trim() ?? "";
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        if (h.Length is 0 or > 253 || h.Any(ch => char.IsControl(ch) || ch is '/' or '\\' or '@' or '?' or '#' or ' ' or '[' or ']')) return null;
        if (h.Contains(':') ? !System.Net.IPAddress.TryParse(h, out _) : Uri.CheckHostName(h) == UriHostNameType.Unknown) return null;
        return h;
    }

    /// <summary>64 hex digits in lower case, whatever separators it was pasted with; null when it is not one.</summary>
    internal static string? CleanFingerprint(string? text)
    {
        var hex = new string((text ?? "").Where(ch => ch is not (':' or ' ' or '-')).ToArray()).ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static string? Clean(string? text, int max)
    {
        var t = new string((text ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return t.Length == 0 ? null : t.Length > max ? t[..max] : t;
    }

    /// <summary>
    /// The settings page's copy of the configuration: Settings may write the password but never read it. It gets
    /// "passwordSet" instead, and a save that leaves the password out keeps the stored one (see <see cref="KeepPassword"/>).
    /// </summary>
    internal static void Redact(System.Text.Json.Nodes.JsonObject config)
    {
        if (config["mqtt"] is not System.Text.Json.Nodes.JsonObject mqtt) return;
        var set = mqtt["password"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var p) && p.Length > 0;
        mqtt.Remove("password");
        mqtt["passwordSet"] = set;
    }

    /// <summary>A saved configuration without a password (the page never had it) keeps the stored one; "" clears it.</summary>
    internal static bool KeepPassword(AgentConfig? proposed, MqttConfig stored)
    {
        // only for the broker it was saved for (the same rule as the test): a save that points the connection somewhere
        // else must not carry the password along to it
        if (proposed?.Mqtt is not { Password: null } mqtt || stored.Password is null || !SameBroker(mqtt, stored)) return false;
        mqtt.Password = stored.Password;
        return true;
    }

    /// <summary>Whether two settings reach the same broker as the same user, the way a password is tied to it.</summary>
    internal static bool SameBroker(MqttConfig a, MqttConfig b)
    {
        static (string?, int, string?, string, string, bool) Identity(MqttConfig c)
        {
            var n = Normalized(new MqttConfig { Host = c.Host, Port = c.Port, Username = c.Username, Transport = c.Transport, WebSocketPath = c.WebSocketPath, Tls = c.Tls });
            return (n.Host, n.Port, n.Username, n.Transport, n.WebSocketPath, n.Tls);
        }
        return Identity(a) == Identity(b);
    }

    /// <summary>
    /// The settings to test: what the page sent, with the stored password when the page sent none. The stored password is
    /// used only against the broker it was saved for, so a test pointed somewhere else cannot be used to collect it.
    /// </summary>
    internal static MqttConfig ForTest(MqttConfig sent, MqttConfig stored)
    {
        var typed = sent.Password is not null; // "" is a password that was removed on the page, not one left out
        var test = Normalized(sent);
        if (!typed && SameBroker(test, stored)) test.Password = stored.Password;
        test.Enabled = true;
        return test;
    }
}

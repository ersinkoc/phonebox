using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;

namespace PhoneBox.Agent;

/// <summary>
/// What one MQTT connection does with the messages it receives: each one that matches a subscription becomes the pushed value
/// of that subscription's key, and nothing else reaches the phone. The keys are the ones in the settings, so no topic can
/// make a key (a wildcard such as "#" cannot fill the push store), and a key is written at most once a second: a chatty
/// topic is one update a second, the last one, not a broadcast per message. The topic and the text of a message are
/// never logged and never stored.
/// </summary>
internal sealed class MqttFeed(IReadOnlyList<MqttSubscription> subscriptions, PushStore push, ILogger log)
{
    /// <summary>The shortest time between two writes of one key.</summary>
    internal const long IntervalMs = 1000;

    private sealed class Slot { public long? WrittenAt; public JsonElement? Waiting; }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = [];
    private bool _refusedLogged;

    /// <summary>A message arrived: the value of every subscription whose topic it matches is written, or kept for the next second.</summary>
    public void Receive(string topic, ReadOnlySequence<byte> payload)
    {
        try
        {
            // a message too large for a pushed value is not even decoded
            if (payload.Length > PushStore.MaxBytes) return;
            string? text = null;
            foreach (var s in subscriptions)
            {
                if (MqttTopicFilterComparer.Compare(topic, s.Topic!) != MqttTopicFilterCompareResult.IsMatch) continue;
                text ??= Strict.GetString(payload);
                if (ValueOf(text, s) is { } value) Offer(s.Key!, value);
            }
        }
        // a binary message, or anything else a broker can send: it is dropped, and no exception reaches the client library
        catch (Exception) { }
    }

    /// <summary>Writes the values that waited for their second; <paramref name="all"/> = even those whose second has not passed.</summary>
    public void Flush(bool all = false)
    {
        lock (_lock)
            foreach (var (key, slot) in _slots)
                if (slot.Waiting is { } value && (all || Due(slot))) Write(key, slot, value);
    }

    private static bool Due(Slot slot) => slot.WrittenAt is not { } at || Environment.TickCount64 - at >= IntervalMs;

    private void Offer(string key, JsonElement value)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var slot)) _slots[key] = slot = new();
            if (Due(slot)) Write(key, slot, value);
            else slot.Waiting = value; // the newest one waits; an older one that waited is replaced
        }
    }

    private void Write(string key, Slot slot, JsonElement value)
    {
        slot.Waiting = null;
        slot.WrittenAt = Environment.TickCount64;
        // the store may refuse (200 keys, a value nested too deeply): one line per connection, without the key or the error
        if (push.Set(key, value, null) is not null && !_refusedLogged)
        {
            _refusedLogged = true;
            log.LogInformation("mqtt: a value was not stored (the push store refused it)");
        }
    }

    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>
    /// The pushed value of a message, or null when there is none to push. With a field the message must be JSON that has it.
    /// Without one, a JSON object or array is kept as it is, and anything else is a number, a boolean or a text, like a
    /// webhook's body. A number or a text with a unit is stored as {"value", "unit"}, which the widgets and rules read.
    /// </summary>
    internal static JsonElement? ValueOf(string payload, MqttSubscription s)
    {
        var text = payload.Trim();
        if (text.Length == 0) return null; // a retained message being cleared
        JsonElement value;
        if (!string.IsNullOrEmpty(s.Field))
        {
            if (!TryJson(text, out var found)) return null;
            foreach (var part in s.Field.Split('.'))
            {
                if (found.ValueKind == JsonValueKind.Object && found.TryGetProperty(part, out var next)) found = next;
                else if (found.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i >= 0 && i < found.GetArrayLength()) found = found[i];
                else return null;
            }
            if (found.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            value = found;
        }
        else if (text[0] is '{' or '[' && TryJson(text, out var whole)) value = whole;
        else value = PushStore.ParseValue(text);
        if (!string.IsNullOrEmpty(s.Unit) && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            value = JsonSerializer.SerializeToElement(new { value, unit = s.Unit });
        return value;
    }

    private static bool TryJson(string text, out JsonElement element)
    {
        try { using var doc = JsonDocument.Parse(text); element = doc.RootElement.Clone(); return true; }
        catch (JsonException) { element = default; return false; }
    }
}

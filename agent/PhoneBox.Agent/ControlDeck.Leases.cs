namespace PhoneBox.Agent;

/// <summary>PTT leases share one original state per microphone, with bounded renewal and disconnect recovery.</summary>
internal sealed class MicrophoneLeases(Func<string, bool?> read, Func<string, bool, bool> write, Func<long> now)
{
    private sealed record Lease(string Device, long Started, long Expires);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Lease> _leases = [];
    private readonly Dictionary<string, bool> _original = [];

    public string? Begin(string device)
    {
        lock (_gate)
        {
            Sweep();
            if (_leases.Count >= 32) return null;
            if (!_original.ContainsKey(device))
            {
                if (read(device) is not { } muted) return null;
                _original[device] = muted;
            }
            if (!write(device, false)) { Restore(device); return null; }
            var id = Guid.NewGuid().ToString("N");
            _leases[id] = new(device, now(), now() + 3000);
            return id;
        }
    }
    public bool Renew(string id)
    {
        lock (_gate)
        {
            Sweep();
            if (!_leases.TryGetValue(id, out var lease) || now() - lease.Started >= 60_000) return false;
            _leases[id] = lease with { Expires = Math.Min(now() + 3000, lease.Started + 60_000) };
            return true;
        }
    }
    public bool End(string id)
    {
        lock (_gate)
        {
            if (!_leases.Remove(id, out var lease)) return false;
            Restore(lease.Device); return true;
        }
    }
    public void Sweep(bool all = false)
    {
        lock (_gate)
        {
            foreach (var id in _leases.Where(p => all || p.Value.Expires <= now()).Select(p => p.Key).ToArray()) _leases.Remove(id);
            foreach (var device in _original.Keys.ToArray()) Restore(device);
        }
    }
    /// <summary>Drops every hold without restoring anything, and returns the mute state each held microphone had before
    /// its hold (true = muted), so a caller can tell a microphone open at rest from one only a hold opened.</summary>
    public IReadOnlyDictionary<string, bool> AbandonAll()
    {
        lock (_gate)
        {
            var original = new Dictionary<string, bool>(_original);
            _leases.Clear();
            _original.Clear();
            return original;
        }
    }
    /// <summary>The microphone was opened for good while held ("Unmute microphones"): ending the hold keeps it open.</summary>
    public void Reopened(string device)
    {
        lock (_gate) if (_original.ContainsKey(device)) _original[device] = false;
    }
    private void Restore(string device)
    {
        if (_leases.Values.Any(l => l.Device == device)) return;
        if (_original.TryGetValue(device, out var original) && write(device, original)) _original.Remove(device);
    }
}

internal sealed class PowerConfirmations(Func<long> now)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Action, long At)> _pending = [];
    public string Arm(string action)
    {
        lock (_gate)
        {
            foreach (var id in _pending.Where(p => now() - p.Value.At > 6000).Select(p => p.Key).ToArray()) _pending.Remove(id);
            if (_pending.Count >= 64) throw new InvalidOperationException("Too many pending confirmations");
            var nonce = Guid.NewGuid().ToString("N"); _pending[nonce] = (action, now()); return nonce;
        }
    }
    public bool Consume(string nonce, string action)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(nonce, out var item) || item.Action != action || now() - item.At is < 1500 or > 6000) return false;
            _pending.Remove(nonce); return true;
        }
    }
}

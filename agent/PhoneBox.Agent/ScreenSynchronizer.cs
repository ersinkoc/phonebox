namespace PhoneBox.Agent;

/// <summary>Serializes setup and polling. Failed devices remain pending for the next poll.</summary>
internal sealed class ScreenSynchronizer(Func<bool?> readLock, Func<string, bool, CancellationToken, Task> apply)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, bool> _applied = [];

    public async Task<bool> ReconcileAsync(string[] devices, CancellationToken ct, bool force = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var old in _applied.Keys.Where(d => !devices.Contains(d)).ToArray()) _applied.Remove(old);
            var available = true;
            List<Exception> failures = [];
            foreach (var device in devices)
            {
                var locked = readLock(); // sample inside the gate, including during USB setup
                if (locked is null) { available = false; _applied.Remove(device); continue; }
                if (!force && _applied.TryGetValue(device, out var previous) && previous == locked) continue;
                try
                {
                    await apply(device, locked.Value, ct);
                    _applied[device] = locked.Value;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { _applied.Remove(device); throw; }
                catch (Exception ex) { _applied.Remove(device); failures.Add(ex); }
            }
            if (failures.Count > 0) throw new AggregateException("Could not synchronize phone screens", failures);
            return available;
        }
        finally { _gate.Release(); }
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { _applied.Clear(); } finally { _gate.Release(); }
    }
}

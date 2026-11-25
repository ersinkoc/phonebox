namespace PhoneBox.Agent;

/// <param name="Source">What the phone charges from: "usb", "ac", "wireless" or null (older app).</param>
/// <param name="Usb">The phone's own view of its USB link: "connected", "configured", "adb" (comma-separated), "none", or null.</param>
public sealed record PhoneBatteryReport(int Level, bool Plugged, double? Temp, long At, string? Source = null, string? Usb = null);

/// <summary>
/// Keeps the phone's battery between two levels (say 50 and 80 %) so a panel that is plugged in all the time does not
/// wear its battery out. Phones cannot stop charging on their own without root, so the power is switched where it comes
/// from: a Home Assistant smart plug (switch entity) or two shortcuts (for example uhubctl for a USB hub with switchable
/// ports, or a smart plug's command line). The phone reports its level every half minute (over USB or Wi-Fi).
/// </summary>
public sealed class ChargeGuard(ConfigStore config, HomeAssistant ha, PushStore push, AudioControl audio, ILogger<ChargeGuard> log) : BackgroundService
{
    /// <summary>A switch command is repeated at most this often if the phone's charging state does not follow.</summary>
    private const long RetryMs = 10 * 60_000;
    /// <summary>No report from the phone for this long while the power is off: switch it back on to be safe.</summary>
    private const long SilentMs = 10 * 60_000;

    private static PhoneBatteryReport? _report;
    private string? _last;      // "on" / "off"
    private long _lastAt;
    private string? _error;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private sealed record HaTarget(string? Entity, string? Url, string? Token);
    private HaTarget? _offHa;

    private static bool SameSupply(HaTarget? a, HaTarget b) => a is not null && a.Entity == b.Entity && a.Url == b.Url;
    private static HaTarget RecoveryTarget(HaTarget owner, HaTarget current) =>
        owner.Url == current.Url && current.Token is { } token ? owner with { Token = token } : owner;

    // The entity and the connection come from one settings snapshot, so a save in between cannot pair an old entity
    // with a new server.
    private static HaTarget Target(AgentConfig cfg) => new(cfg.Charge.Entity, cfg.HomeAssistant.Url, cfg.HomeAssistant.Token);

    /// <summary>Whether the supply a test asked for (its settings and server) is still the configured one.</summary>
    private static bool SameSupplySettings(ChargeConfig requested, string? requestedUrl, AgentConfig current) =>
        requested.Method == current.Charge.Method && requested.Entity == current.Charge.Entity
        && requested.OnShortcut == current.Charge.OnShortcut && requested.OffShortcut == current.Charge.OffShortcut
        && (requested.Method == "shortcuts" || requestedUrl == current.HomeAssistant.Url);

    /// <summary>Called by the phone (POST /api/phone/battery).</summary>
    public static void Report(int level, bool plugged, double? temp, string? source = null, string? usb = null) =>
        _report = new PhoneBatteryReport(Math.Clamp(level, 0, 100), plugged, temp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            string.IsNullOrWhiteSpace(source) ? null : source.Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(usb) ? null : usb.Trim().ToLowerInvariant());

    /// <summary>The phone's latest report (over USB or Wi-Fi), or null.</summary>
    public static PhoneBatteryReport? LastReport => _report;

    public object Status() => new
    {
        phone = _report,
        power = _last,
        switchedAt = _lastAt == 0 ? (long?)null : _lastAt,
        error = _error,
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(5000, ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Check(); }
            catch (Exception ex) { log.LogWarning("charge guard: {Msg}", ex.Message); }
            await Task.Delay(30_000, ct);
        }
    }

    /// <summary>What to do for a battery reading: "on", "off" or null (leave the power as it is).</summary>
    public static string? Decide(ChargeConfig c, PhoneBatteryReport? r, long now, string? last, long lastAt)
    {
        if (r is null || now - r.At is < 0 or > SilentMs) return last == "off" ? "on" : null; // lost the phone: never leave it unpowered
        var retry = now - lastAt > RetryMs;
        if (r.Level >= c.Max && r.Plugged && (last != "off" || retry)) return "off";
        if (r.Level <= c.Min && !r.Plugged && (last != "on" || retry)) return "on";
        return null;
    }

    private async Task Check()
    {
        await _switchGate.WaitAsync();
        try
        {
            var cfg = config.Get();
            var c = cfg.Charge;
            var target = Target(cfg);
            if (_offHa is { } owner && (!c.Enabled || c.Method == "shortcuts" || !SameSupply(owner, target)
                || (owner.Token is not null && target.Token is null)))
            {
                // Recover the supply we cut before acting on replacement settings.
                if (await SwitchCore(new ChargeConfig { Method = "ha" }, "on", RecoveryTarget(owner, target)) is null
                    && (c.Method == "shortcuts" || !SameSupply(owner, target)))
                { _last = null; _lastAt = 0; }
                return;
            }
            if (!c.Enabled) { if (_last == "off") await SwitchCore(c, "on", target); return; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // A test "on" for another supply can leave _last at "on" while this one is still tracked off; the tracked
            // supply decides, so a phone that stops reporting still gets its power back.
            var last = SameSupply(_offHa, target) ? "off" : _last;
            if (Decide(c, _report, now, last, _lastAt) is { } action) await SwitchCore(c, action, target);
        }
        finally { _switchGate.Release(); }
    }

    public async Task<string?> Switch(ChargeConfig c, string action)
    {
        var requestedUrl = config.Get().HomeAssistant.Url;
        await _switchGate.WaitAsync();
        try
        {
            // A test that waited behind another switch must not reach a supply the settings replaced meanwhile.
            var cfg = config.Get();
            if (!SameSupplySettings(c, requestedUrl, cfg)) return "the power settings changed; test the new supply again";
            c = cfg.Charge;
            var target = Target(cfg);
            if (action == "off" && _offHa is { } owner && (c.Method == "shortcuts" || !SameSupply(owner, target)))
                if (await SwitchCore(new ChargeConfig { Method = "ha" }, "on", RecoveryTarget(owner, target)) is { } error) return error;
            return await SwitchCore(c, action, target);
        }
        finally { _switchGate.Release(); }
    }

    private async Task<string?> SwitchCore(ChargeConfig c, string action, HaTarget target)
    {
        string? error;
        var submitted = false;
        if (c.Method == "shortcuts")
        {
            var id = action == "on" ? c.OnShortcut : c.OffShortcut;
            var cfg = config.Get();
            submitted = cfg.Shortcuts.Any(s => s.Id == id);
            error = string.IsNullOrWhiteSpace(id) ? $"no shortcut set for power {action}" : Shortcuts.Run(cfg, id, log, push, audio);
        }
        else if (string.IsNullOrWhiteSpace(target.Entity)) error = "no Home Assistant switch set";
        else
        {
            var recovery = action == "on" && SameSupply(_offHa, target) ? _offHa : null;
            var result = await ha.SwitchAsync(target.Entity, action == "on",
                new HomeAssistantConfig { Url = target.Url, Token = target.Token ?? recovery?.Token });
            error = result.Error;
            submitted = result.Submitted;
            // A failed response can follow a successful switch; retain that possible off target too.
            if (action == "off" && result.Submitted) _offHa = target;
            if (action == "on" && error is null && SameSupply(_offHa, target)) _offHa = null;
        }
        // a failed "on" leaves the plug off: keep the old state so the next pass asks again (never leave the phone unpowered)
        if (error is null || (action == "off" && submitted)) _last = action;
        _lastAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _error = error;
        if (error is null) log.LogInformation("charge guard: power {Action} (phone at {Level}%)", action, _report?.Level);
        else log.LogWarning("charge guard: power {Action} failed: {Error}", action, error);
        return error;
    }
}

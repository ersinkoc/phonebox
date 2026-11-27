using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Parses natural voice commands from the Dictation widget when Voice Assistant mode is enabled
/// (e.g., "open chrome and search elon musk", "open spotify", "close notepad", "mute mic").
/// Falls back to normal dictation typing when the spoken text is not a recognized command.
/// </summary>
public sealed partial class VoiceAssistant(ConfigStore config, ProcessMonitor procs, PushStore push, AudioControl audio, ILogger<VoiceAssistant> log)
{
    [GeneratedRegex(@"^(?:open|launch|start)\s+(?<app>.+?)\s+(?:and\s+)?search(?:\s+for)?\s+(?<query>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAndSearchRx();

    [GeneratedRegex(@"^(?:search\s+youtube(?:\s+for)?|youtube)\s+(?<query>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex YoutubeSearchRx();

    [GeneratedRegex(@"^(?:search(?:\s+for)?|google)\s+(?<query>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WebSearchRx();

    [GeneratedRegex(@"^(?:open|launch|start)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAppRx();

    [GeneratedRegex(@"^(?:close|quit|exit|kill)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CloseAppRx();

    public Task<(bool Handled, int Status, string? Message)> TryExecuteAsync(string? deviceId, string? widget, string? text, CancellationToken ct)
    {
        var w = PanelWidgets.Find(config.Get(), deviceId, widget, Dictation.WidgetType);
        if (w is null) return Task.FromResult((false, 404, (string?)null));
        var assistantOn = w.Bool("assistant") == true;
        if (!assistantOn || w.Bool("control") != true)
            return Task.FromResult((false, 200, (string?)null));

        var raw = Dictation.Clean(text).Trim();
        if (raw.Length == 0) return Task.FromResult((false, 400, (string?)null));

        var cfg = config.Get();

        // 1. "open <app> and search <query>"
        var m = OpenAndSearchRx().Match(raw);
        if (m.Success)
        {
            var app = m.Groups["app"].Value.Trim();
            var query = m.Groups["query"].Value.Trim();
            var url = app.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                ? "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query)
                : "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
            var err = OpenUrlInApp(cfg, app, url);
            return Task.FromResult(err is null
                ? (true, 200, (string?)$"Searching {query}")
                : (true, 500, (string?)err));
        }

        // 2. "youtube <query>" / "search youtube <query>"
        m = YoutubeSearchRx().Match(raw);
        if (m.Success)
        {
            var query = m.Groups["query"].Value.Trim();
            var err = OpenUrlInApp(cfg, "chrome", "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
            return Task.FromResult(err is null
                ? (true, 200, (string?)$"YouTube: {query}")
                : (true, 500, (string?)err));
        }

        // 3. "search <query>" / "google <query>"
        m = WebSearchRx().Match(raw);
        if (m.Success)
        {
            var query = m.Groups["query"].Value.Trim();
            var err = OpenUrlInApp(cfg, "chrome", "https://www.google.com/search?q=" + Uri.EscapeDataString(query));
            return Task.FromResult(err is null
                ? (true, 200, (string?)$"Searching {query}")
                : (true, 500, (string?)err));
        }

        // 4. Built-in quick commands
        var lower = raw.ToLowerInvariant();
        if (lower is "mute mic" or "unmute mic" or "toggle mic")
        {
            var micSc = cfg.Shortcuts.FirstOrDefault(s => s.Cmd == PhoneDevices.MicCmd);
            if (micSc is not null)
            {
                var err = Shortcuts.Run(cfg, micSc.Id, log, push, audio);
                return Task.FromResult((true, err is null ? 200 : 500, err ?? "Toggled mic"));
            }
        }
        if (lower is "mute" or "unmute" or "toggle mute")
        {
            var muteSc = cfg.Shortcuts.FirstOrDefault(s => s.Cmd == "builtin:mute");
            if (muteSc is not null)
            {
                var err = Shortcuts.Run(cfg, muteSc.Id, log, push, audio);
                return Task.FromResult((true, err is null ? 200 : 500, err ?? "Toggled mute"));
            }
        }
        if (lower is "lock" or "lock pc" or "lock computer")
        {
            var lockSc = cfg.Shortcuts.FirstOrDefault(s => s.Cmd == "builtin:lock");
            if (lockSc is not null)
            {
                var err = Shortcuts.Run(cfg, lockSc.Id, log, push, audio);
                return Task.FromResult((true, err is null ? 200 : 500, err ?? "Locked PC"));
            }
        }
        if (lower is "screenshot" or "snip" or "take screenshot")
        {
            StartShell("ms-screenclip:", null);
            return Task.FromResult((true, 200, (string?)"Opened Snip"));
        }

        // 5. "open <app>"
        m = OpenAppRx().Match(raw);
        if (m.Success)
        {
            var target = m.Groups["target"].Value.Trim();
            var sc = FindShortcut(cfg, target, isClose: false);
            if (sc is not null)
            {
                var err = Shortcuts.Run(cfg, sc.Id, log, push, audio);
                return Task.FromResult((true, err is null ? 200 : 500, err ?? $"Opened {sc.Label}"));
            }
        }

        // 6. "close <app>"
        m = CloseAppRx().Match(raw);
        if (m.Success)
        {
            var target = m.Groups["target"].Value.Trim();
            var sc = FindShortcut(cfg, target, isClose: true);
            if (sc is not null)
            {
                var err = Shortcuts.Run(cfg, sc.Id, log, push, audio);
                return Task.FromResult((true, err is null ? 200 : 500, err ?? $"Closed {target}"));
            }
            if (cfg.AllowKill())
            {
                var kr = procs.Kill(target);
                if (kr.Killed > 0) return Task.FromResult((true, 200, (string?)$"Closed {target}"));
            }
        }

        return Task.FromResult((false, 200, (string?)null));
    }

    private static Shortcut? FindShortcut(AgentConfig cfg, string query, bool isClose)
    {
        var q = query.Trim().ToLowerInvariant();
        var candidates = cfg.Shortcuts.Where(s => isClose ? s.Id.StartsWith("close-", StringComparison.OrdinalIgnoreCase) : !s.Id.StartsWith("close-", StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.FirstOrDefault(s => s.Id.Equals(isClose ? "close-" + q : q, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => s.Label.Equals(isClose ? "Close " + q : q, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => s.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private string? OpenUrlInApp(AgentConfig cfg, string appName, string url)
    {
        var sc = FindShortcut(cfg, appName, isClose: false);
        if (sc is not null && !string.IsNullOrWhiteSpace(sc.Cmd) && !sc.Cmd.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
        {
            var args = string.IsNullOrWhiteSpace(sc.Args) ? $"\"{url}\"" : $"{sc.Args} \"{url}\"";
            return StartShell(sc.Cmd, args);
        }
        return StartShell(url, null);
    }

    private static string? StartShell(string file, string? args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { Arguments = args ?? "", UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed partial class ControlDeck
{
    private readonly Lock _mixerGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _actionResults = new();
    private nint _mixerLibrary;
    private string? _mixerPath;
    private VmLogin? _vmLogout, _vmDirty;
    private VmGet? _vmGet;
    private VmSet? _vmSet;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int VmLogin();
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] private delegate int VmGet([MarshalAs(UnmanagedType.LPStr)] string name, out float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] private delegate int VmSet([MarshalAs(UnmanagedType.LPStr)] string name, float value);

    private async Task<DeckResult> Workflow(DeckAction action, CancellationToken ct)
    {
        if (!Regex.IsMatch(action.Target, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")
            || !Regex.IsMatch(action.Argument ?? "", @"\A[A-Za-z0-9_.-]+\z")) return new(false, "Configure a repository and workflow filename");
        var gh = ToolProcess.Find("gh"); if (gh is null) return new(false, "Install and sign in to GitHub CLI on the computer");
        var start = new ProcessStartInfo(gh);
        foreach (var arg in new[] { "workflow", "run", action.Argument!, "--repo", action.Target }) start.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(action.Device)) { start.ArgumentList.Add("--ref"); start.ArgumentList.Add(action.Device); }
        var result = await ToolProcess.RunAsync(start, TimeSpan.FromSeconds(20), ct);
        _actionResults[action.Id] = result.ExitCode == 0 ? "Workflow dispatched" : "Dispatch failed";
        return new(result.ExitCode == 0, result.ExitCode == 0 ? null : "GitHub refused the workflow; check the computer's GitHub CLI account");
    }
    private async Task<DeckResult> RouteAudio(DeckAction action, CancellationToken ct)
    {
        var path = _config.Get().Controls.MixerExecutable;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new(false, "Configure SoundVolumeView on the computer to use application routing");
        if (string.IsNullOrWhiteSpace(action.Target) || action.Target.Length > 256 || action.Device is null
            || !Audio().Devices.Any(d => d.Id == action.Device && d.Direction == "output"))
            return new(false, "Choose an application and an active output device");
        var start = new ProcessStartInfo(path);
        foreach (var arg in new[] { "/SetAppDefault", action.Device, "all", action.Target }) start.ArgumentList.Add(arg);
        var result = await ToolProcess.RunAsync(start, TimeSpan.FromSeconds(10), ct);
        _actionResults[action.Id] = result.ExitCode == 0 ? "Routing requested" : "Routing request failed";
        return new(result.ExitCode == 0, result.ExitCode == 0 ? null : "The application routing request failed");
    }

    internal static bool MixerParameter(string name) => Regex.IsMatch(name,
        @"\A(?:Strip|Bus)\[(?:[0-9]|[1-5][0-9]|6[0-3])\]\.(?:Gain|Mute|Solo|Mono|A[1-5]|B[1-3])\z", RegexOptions.CultureInvariant);
    private bool ConnectMixer()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var path = _config.Get().Controls.MixerLibrary;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (_mixerLibrary != 0 && path == _mixerPath) return true;
        DisposeMixer();
        try
        {
            _mixerLibrary = NativeLibrary.Load(path); _mixerPath = path;
            T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_mixerLibrary, name));
            var login = Load<VmLogin>("VBVMR_Login");
            _vmLogout = Load<VmLogin>("VBVMR_Logout"); _vmDirty = Load<VmLogin>("VBVMR_IsParametersDirty");
            _vmGet = Load<VmGet>("VBVMR_GetParameterFloat"); _vmSet = Load<VmSet>("VBVMR_SetParameterFloat");
            if (login() < 0) { DisposeMixer(); return false; }
            return true;
        }
        catch { DisposeMixer(); return false; }
    }
    private string? MixerValue(DeckAction action)
    {
        lock (_mixerGate)
        {
            if (!MixerParameter(action.Target) || !ConnectMixer() || _vmDirty!() < 0 || _vmGet!(action.Target, out var value) != 0 || !float.IsFinite(value)) return null;
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
    private DeckResult MixerAction(DeckAction action, int? value)
    {
        lock (_mixerGate)
        {
            if (!MixerParameter(action.Target) || !ConnectMixer() || _vmDirty!() < 0) return new(false, "Configure the installed Voicemeeter Remote DLL and start its audio engine");
            var level = action.Target.EndsWith(".Gain", StringComparison.Ordinal) ? Math.Clamp(value ?? action.Value, -60, 12) : Math.Clamp(value ?? action.Value, 0, 1);
            return _vmSet!(action.Target, level) == 0 ? new(true) : new(false, "Voicemeeter refused the parameter");
        }
    }
    private void DisposeMixer()
    {
        lock (_mixerGate)
        {
            if (_mixerLibrary == 0) return;
            try { _vmLogout?.Invoke(); } catch { }
            NativeLibrary.Free(_mixerLibrary); _mixerLibrary = 0; _mixerPath = null; _vmGet = null; _vmSet = null; _vmLogout = null; _vmDirty = null;
        }
    }
    private DeckItem Describe(DeckAction action)
    {
        var state = action.Kind is "task" or "server" ? _tasks.State(action.Id) : _actionResults.GetValueOrDefault(action.Id);
        var available = true;
        if (action.Kind == "service") { state = _native.ActionState(action) ?? "Service unavailable"; available = state != "Service unavailable"; }
        if (action.Kind is "sound" or "radio")
        {
            if (action.Kind == "radio")
            {
                if (_radioGate.TryEnter())
                {
                    try
                    {
                        state = _native.ActionState(action) ?? "Playback requires Windows";
                        if (state is not "Playing" and not "Playback requires Windows") OnNativeSoundStoppedCore(action.Id);
                    }
                    finally { _radioGate.Exit(); }
                }
                else state = IsRadioPlaying(action) ? "Playing" : "Stopped";
            }
            else state = _native.ActionState(action) ?? "Playback requires Windows";
            available = OperatingSystem.IsWindows();
        }
        if (action.Kind == "workflow" && ToolProcess.Find("gh") is null) { available = false; state = "Install GitHub CLI"; }
        if (action.Kind == "playlist" && string.IsNullOrWhiteSpace(_config.Get().Controls.SpotifyClientId) && string.IsNullOrWhiteSpace(_config.Get().Controls.SpotifyToken)) { available = false; state = "Connect Spotify in desktop settings"; }
        if (action.Kind == "midi" && (!int.TryParse(action.Device, out var midiPort) || !_midi.Any(p => p.Id == midiPort.ToString()))) { available = false; state = "MIDI port unavailable"; }
        if (action.Kind is "app" or "project" or "shortcut" or "focus")
        {
            var shortcut = _config.Get().Shortcuts.FirstOrDefault(s => s.Id == action.Target);
            available = shortcut is not null; state = shortcut is null ? "Shortcut is missing" : ShortcutRunning(shortcut) ? "Running" : "Ready";
        }
        if (action.Kind is "mixer" or "mixer-mute") { state = MixerValue(action); available = state is not null; state ??= "Mixer is not connected"; }
        if (action.Kind == "audio-route" && (string.IsNullOrWhiteSpace(_config.Get().Controls.MixerExecutable) || !File.Exists(_config.Get().Controls.MixerExecutable)))
        { available = false; state = "Configure SoundVolumeView"; }
        return new(action.Id, action.Label, action.Kind, state, available);
    }
}

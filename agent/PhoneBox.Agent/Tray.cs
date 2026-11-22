using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Resources;
using System.Windows.Forms;

namespace PhoneBox.Agent;

/// <summary>System tray icon: status, settings, phone camera, logs, start with Windows, quit.</summary>
public sealed class Tray : IHostedService
{
    private readonly string SettingsUrl;

    private readonly IHostApplicationLifetime _life;
    private readonly AdbReverse _adb;
    private readonly ConfigStore _config;
    private readonly PhoneCamera _camera;
    private readonly NativeCamera _native;
    private readonly int _port;
    private static readonly ResourceManager Strings = new("PhoneBox.Agent.TrayStrings", typeof(Tray).Assembly);
    private Thread? _thread;
    private ApplicationContext? _ctx;
    private Control? _invoker;
    private NotifyIcon? _icon;

    public Tray(IHostApplicationLifetime life, AdbReverse adb, AgentEndpoint endpoint, ConfigStore config, PhoneCamera camera, NativeCamera native)
    {
        SettingsUrl = endpoint.SettingsUrl;
        _port = endpoint.Port;
        _camera = camera;
        _native = native;
        _life = life;
        _adb = adb;
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        try { _invoker?.BeginInvoke(() => { if (_icon != null) _icon.Visible = false; _ctx?.ExitThread(); }); } catch { }
        return Task.CompletedTask;
    }

    private CultureInfo UiCulture() => _config.Get().Display.Language switch
    {
        "auto" => CultureInfo.CurrentUICulture,
        "zh" => CultureInfo.GetCultureInfo("zh-Hans"),
        var language => CultureInfo.GetCultureInfo(language),
    };

    private static string Text(string key, CultureInfo culture) =>
        Strings.GetString(key, culture) ?? throw new MissingManifestResourceException(key);

    private string Tooltip(CultureInfo culture) =>
        Text(_adb.Connected ? "tooltipConnected" : "tooltipWaiting", culture);

    private void Run()
    {
        Application.EnableVisualStyles();
        // Invisible control used to marshal calls onto this UI thread (its handle is created here).
        var host = new Control();
        _ = host.Handle;
        _invoker = host;
        _ctx = new ApplicationContext();

        using var iconStream = AppPaths.Resource("phonebox.ico");
        var status = new ToolStripMenuItem { Enabled = false };
        var autostart = new ToolStripMenuItem();
        autostart.Click += (_, _) => { ToggleAutostart(UiCulture()); autostart.Checked = File.Exists(AppPaths.StartupShortcut); };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"PhoneBox {AppPaths.Version}") { Enabled = false });
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        var settings = new ToolStripMenuItem("", null, (_, _) => OpenUrl(SettingsUrl)) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(settings);
        // cancelled while Windows is ending the session (no adb is started then, for at most a minute): nothing to show
        var openPanel = menu.Items.Add("", null, async (_, _) => { try { await _adb.OpenPanelAsync(); } catch (OperationCanceledException) { } });
        // One click: enable PhoneBox Camera if needed and ask the USB phone. The phone still has to approve the session.
        var camera = menu.Items.Add("", null, async (_, _) => await ToggleCameraAsync(UiCulture()));
        menu.Items.Add(new ToolStripSeparator());
        var editConfig = menu.Items.Add("", null, (_, _) => Open("notepad.exe", AppPaths.ConfigPath));
        var openLog = menu.Items.Add("", null, (_, _) => Open("notepad.exe", AppPaths.LogPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        var quit = menu.Items.Add("", null, (_, _) => _life.StopApplication());
        menu.Opening += (_, _) =>
        {
            var culture = UiCulture();
            menu.RightToLeft = culture.TextInfo.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
            settings.Text = Text("settings", culture);
            openPanel.Text = Text("openPanel", culture);
            camera.Text = Text(_camera.Active ? "cameraStop" : "cameraStart", culture);
            editConfig.Text = Text("editConfig", culture);
            openLog.Text = Text("openLog", culture);
            autostart.Text = Text("autostart", culture);
            quit.Text = Text("quit", culture);
            status.Text = _adb.Connected
                ? string.Format(culture, Text("connected", culture), _adb.Devices)
                : Text("disconnected", culture);
            _icon!.Text = Tooltip(culture);
            autostart.Checked = File.Exists(AppPaths.StartupShortcut);
        };

        _icon = new NotifyIcon
        {
            Icon = iconStream is null ? SystemIcons.Application : new Icon(iconStream, 32, 32),
            Text = Tooltip(UiCulture()),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenUrl(SettingsUrl);
        _adb.StateChanged += () =>
        {
            try { host.BeginInvoke(() => _icon.Text = Tooltip(UiCulture())); } catch { }
        };

        Application.Run(_ctx);
        _icon.Visible = false;
        _icon.Dispose();
    }

    private bool _cameraBusy;

    /// <summary>Runs on the tray's UI thread; awaits resume there.</summary>
    private async Task ToggleCameraAsync(CultureInfo culture)
    {
        if (_cameraBusy) return;
        if (_camera.Active) { _camera.Stop(); return; }
        _cameraBusy = true;
        try
        {
            var phone = PhoneCamera.UsbPhone(PhoneRegistry.All(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (phone is null) { Notify(Text("cameraNoPhone", culture), ToolTipIcon.Warning); return; }
            if (NativeCamera.Supported)
            {
                var state = _native.Status();
                if (!state.Installed)
                {
                    // Installing asks for administrator approval: that stays an explicit click in the settings.
                    Notify(Text("cameraInstall", culture), ToolTipIcon.Warning);
                    OpenUrl(SettingsUrl + "#general");
                    return;
                }
                if (!state.Running && await _native.Enable(_port) is { } error) { Notify(error, ToolTipIcon.Error); return; }
            }
            if (_camera.Request(phone)) Notify(Text("cameraAsk", culture), ToolTipIcon.Info);
        }
        finally { _cameraBusy = false; }
    }

    private void Notify(string text, ToolTipIcon icon)
    {
        try { _icon?.ShowBalloonTip(5000, "PhoneBox", text, icon); } catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void Open(string app, string path)
    {
        try { Process.Start(new ProcessStartInfo(app, $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Adds/removes a Startup-folder shortcut (only when the user clicks the menu item).</summary>
    private static void ToggleAutostart(CultureInfo culture)
    {
        try
        {
            if (File.Exists(AppPaths.StartupShortcut)) { File.Delete(AppPaths.StartupShortcut); return; }
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(AppPaths.StartupShortcut);
            lnk.TargetPath = Environment.ProcessPath;
            lnk.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            lnk.Description = "PhoneBox agent";
            lnk.Save();
        }
        catch (Exception ex)
        {
            var options = culture.TextInfo.IsRightToLeft ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0;
            MessageBox.Show(string.Format(culture, Text("autostartError", culture), ex.Message), "PhoneBox",
                MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, options);
        }
    }
}

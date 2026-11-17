using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PhoneBox.Agent;

/// <summary>System tray icon: status, settings, logs, start with Windows, quit.</summary>
public sealed class Tray : IHostedService
{
    private readonly string SettingsUrl;

    private readonly IHostApplicationLifetime _life;
    private readonly AdbReverse _adb;
    private Thread? _thread;
    private ApplicationContext? _ctx;
    private Control? _invoker;
    private NotifyIcon? _icon;

    public Tray(IHostApplicationLifetime life, AdbReverse adb, AgentEndpoint endpoint)
    {
        SettingsUrl = endpoint.SettingsUrl;
        _life = life;
        _adb = adb;
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
        var autostart = new ToolStripMenuItem("Start with Windows");
        autostart.Click += (_, _) => { ToggleAutostart(); autostart.Checked = File.Exists(AppPaths.StartupShortcut); };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"PhoneBox {AppPaths.Version}") { Enabled = false });
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenUrl(SettingsUrl)) { Font = new Font(menu.Font, FontStyle.Bold) });
        // cancelled while Windows is ending the session (no adb is started then, for at most a minute): nothing to show
        menu.Items.Add("Open panel on phone", null, async (_, _) => { try { await _adb.OpenPanelAsync(); } catch (OperationCanceledException) { } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Edit config.json", null, (_, _) => Open("notepad.exe", AppPaths.ConfigPath));
        menu.Items.Add("Open log", null, (_, _) => Open("notepad.exe", AppPaths.LogPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _life.StopApplication());
        menu.Opening += (_, _) =>
        {
            status.Text = _adb.Connected ? $"Phone connected ({_adb.Devices})" : "No phone connected";
            autostart.Checked = File.Exists(AppPaths.StartupShortcut);
        };

        _icon = new NotifyIcon
        {
            Icon = iconStream is null ? SystemIcons.Application : new Icon(iconStream, 32, 32),
            Text = "PhoneBox",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenUrl(SettingsUrl);
        _adb.StateChanged += () =>
        {
            try { host.BeginInvoke(() => _icon.Text = _adb.Connected ? "PhoneBox — phone connected" : "PhoneBox — waiting for phone"); } catch { }
        };

        Application.Run(_ctx);
        _icon.Visible = false;
        _icon.Dispose();
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
    private static void ToggleAutostart()
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
            MessageBox.Show("Could not change the startup shortcut: " + ex.Message, "PhoneBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

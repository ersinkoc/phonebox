// PhoneBox in the GNOME top bar.
//
// The indicator shows CPU and the Claude session quota from the local agent. Clicking it shows the PhoneBox desktop
// widget (extras/gnome-widget, started with --dropdown) right below the indicator; clicking it again, or anywhere
// outside the panel, hides it. The widget shows and hides itself (actions over D-Bus, as a Gtk.Application) rather
// than being minimized, so minimize animations never play.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Soup from 'gi://Soup';
import St from 'gi://St';
import Clutter from 'gi://Clutter';
import GObject from 'gi://GObject';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';

const AGENT = 'http://127.0.0.1:8765';
const WIDGET_BUS_NAME = 'net.oxog.phonebox.Widget';
const WIDGET_OBJECT_PATH = '/net/oxog/phonebox/Widget';
const WIDGET_WM_CLASS = 'phonebox-widget';
const STATE_SECONDS = 2;
const QUOTA_SECONDS = 30;
// a click on the indicator also takes focus away from the panel; this keeps that click from reopening it
const REOPEN_GUARD_MS = 350;

function widgetCommand() {
    const local = GLib.build_filenamev([GLib.get_home_dir(), '.local', 'bin', 'phonebox-widget']);
    return GLib.file_test(local, GLib.FileTest.IS_EXECUTABLE) ? local : 'phonebox-widget';
}

function findWidgetWindow() {
    return global.get_window_actors()
        .map(a => a.meta_window)
        .find(w => w && w.get_wm_class_instance?.() === WIDGET_WM_CLASS && !w.minimized) ?? null;
}

const Indicator = GObject.registerClass(
class PhoneBoxIndicator extends PanelMenu.Button {
    _init() {
        super._init(0.5, 'PhoneBox', true);
        this._label = new St.Label({
            text: 'PhoneBox',
            y_align: Clutter.ActorAlign.CENTER,
            style_class: 'phonebox-indicator-label phonebox-indicator-offline',
        });
        this.add_child(this._label);

        this._session = new Soup.Session({timeout: 4});
        this._cancellable = new Gio.Cancellable();
        this._cpu = null;
        this._claude = null;
        this._online = false;
        this._lastAutoHide = 0;

        this._stateTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, STATE_SECONDS, () => {
            this._pollState();
            return GLib.SOURCE_CONTINUE;
        });
        this._quotaTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, QUOTA_SECONDS, () => {
            this._pollQuotas();
            return GLib.SOURCE_CONTINUE;
        });
        this._pollState();
        this._pollQuotas();

        // clicking outside the panel hides it
        this._focusId = global.display.connect('notify::focus-window', () => this._onFocusChanged());
        // the widget places itself where it is told; this corrects it when the window manager moved it anyway
        this._createdId = global.display.connect('window-created', (_d, win) => this._onWindowCreated(win));

        // start the widget hidden now, so the first click opens a panel that has already loaded
        this._callWidget('preload', null, () => this._spawnWidget([]));
    }

    // ------------------------------------------------------------- data
    _get(path, onJson) {
        const msg = Soup.Message.new('GET', AGENT + path);
        this._session.send_and_read_async(msg, GLib.PRIORITY_DEFAULT, this._cancellable, (session, res) => {
            let json = null;
            try {
                const bytes = session.send_and_read_finish(res);
                if (msg.get_status() === Soup.Status.OK)
                    json = JSON.parse(new TextDecoder().decode(bytes.get_data()));
            } catch (e) {
                if (e.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.CANCELLED))
                    return;
            }
            onJson(json);
        });
    }

    _pollState() {
        this._get('/api/state', json => {
            this._online = !!json;
            this._cpu = json?.pc?.cpu ?? null;
            this._render();
        });
    }

    _pollQuotas() {
        this._get('/api/quotas', json => {
            const claude = json?.providers?.find(p => p.id === 'claude');
            this._claude = claude?.limits?.[0]?.percent ?? null;
            this._render();
        });
    }

    _render() {
        const parts = [];
        if (this._cpu !== null)
            parts.push(`CPU ${Math.round(this._cpu)}%`);
        if (this._claude !== null)
            parts.push(`Claude ${Math.round(this._claude)}%`);
        this._label.text = this._online && parts.length ? parts.join(' · ') : 'PhoneBox';
        if (this._online)
            this._label.remove_style_class_name('phonebox-indicator-offline');
        else
            this._label.add_style_class_name('phonebox-indicator-offline');
    }

    // ------------------------------------------------------------- panel
    vfunc_event(event) {
        const type = event.type();
        if (type === Clutter.EventType.BUTTON_RELEASE || type === Clutter.EventType.TOUCH_END) {
            if (event.type() === Clutter.EventType.BUTTON_RELEASE && event.get_button() === Clutter.BUTTON_SECONDARY)
                this._openSettings();
            else
                this._toggle();
            return Clutter.EVENT_STOP;
        }
        return Clutter.EVENT_PROPAGATE;
    }

    _toggle() {
        if (findWidgetWindow()) {
            this._hide();
            return;
        }
        if (Date.now() - this._lastAutoHide < REOPEN_GUARD_MS)
            return;
        this._show();
    }

    // where the panel goes: under the indicator, right-aligned to it, kept on the indicator's monitor
    _target() {
        const [x, y] = this.get_transformed_position();
        const [w, h] = this.get_transformed_size();
        const monitor = Main.layoutManager.findMonitorForActor(this) ?? Main.layoutManager.primaryMonitor;
        const width = this._widgetWidth ?? 420;
        const left = Math.max(monitor.x, Math.min(Math.round(x + w - width), monitor.x + monitor.width - width));
        return [left, Math.round(y + h)];
    }

    _show() {
        const [x, y] = this._target();
        const param = new GLib.Variant('(ii)', [x, y]);
        this._callWidget('show', param, () => this._spawnWidget(['--show-at', `${x}`, `${y}`]));
    }

    _hide() {
        this._callWidget('hide', null, null);
    }

    _onFocusChanged() {
        const panel = findWidgetWindow();
        if (!panel)
            return;
        const focus = global.display.focus_window;
        if (focus === panel || focus?.get_transient_for?.() === panel)
            return;
        this._lastAutoHide = Date.now();
        this._hide();
    }

    _onWindowCreated(win) {
        if (win.get_wm_class_instance?.() !== WIDGET_WM_CLASS)
            return;
        // the frame rectangle is only right once the window is shown
        const id = win.connect('shown', () => {
            win.disconnect(id);
            const rect = win.get_frame_rect();
            this._widgetWidth = rect.width;
            const [x, y] = this._target();
            if (rect.x !== x || rect.y !== y)
                win.move_frame(true, x, y);
            win.activate(global.get_current_time());
        });
    }

    _openSettings() {
        Gio.AppInfo.launch_default_for_uri(AGENT + '/', null);
    }

    // ------------------------------------------------------------- the widget process
    // org.gtk.Actions is what every Gtk.Application exports; a missing bus name means the widget is not running
    _callWidget(action, param, onMissing) {
        Gio.DBus.session.call(
            WIDGET_BUS_NAME, WIDGET_OBJECT_PATH, 'org.gtk.Actions', 'Activate',
            new GLib.Variant('(sava{sv})', [action, param ? [param] : [], {}]),
            null, Gio.DBusCallFlags.NO_AUTO_START, 2000, this._cancellable,
            (conn, res) => {
                try {
                    conn.call_finish(res);
                } catch (e) {
                    if (e.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.CANCELLED))
                        return;
                    if (onMissing)
                        onMissing();
                    else
                        console.debug(`PhoneBox: ${action}: ${e.message}`);
                }
            });
    }

    // One start at a time: the widget takes a moment to claim its bus name, and clicks meanwhile must not start more.
    // A start with --show-at shows itself once it is up.
    _spawnWidget(extra) {
        if (this._starting)
            return;
        try {
            const proc = Gio.Subprocess.new([widgetCommand(), '--dropdown', ...extra], Gio.SubprocessFlags.NONE);
            this._starting = proc;
            const done = () => {
                if (this._starting === proc)
                    this._starting = null;
            };
            proc.wait_async(null, done);
            // up and on the bus (or failed): later clicks talk to it, or try again
            let watch = 0;
            const stop = () => {
                if (watch)
                    Gio.bus_unwatch_name(watch);
                watch = 0;
                done();
            };
            watch = Gio.bus_watch_name(Gio.BusType.SESSION, WIDGET_BUS_NAME, Gio.BusNameWatcherFlags.NONE,
                stop, null);
            GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 30, () => {
                stop();
                return GLib.SOURCE_REMOVE;
            });
        } catch (e) {
            Main.notifyError('PhoneBox', `Could not start the PhoneBox widget (${e.message}). Install it from extras/gnome-widget.`);
        }
    }

    destroy() {
        this._cancellable.cancel();
        GLib.source_remove(this._stateTimer);
        GLib.source_remove(this._quotaTimer);
        global.display.disconnect(this._focusId);
        global.display.disconnect(this._createdId);
        this._session.abort();
        // leave the widget running hidden: disabling happens on every screen lock
        super.destroy();
    }
});

export default class PhoneBoxExtension extends Extension {
    enable() {
        this._indicator = new Indicator();
        Main.panel.addToStatusArea(this.uuid, this._indicator, 0, 'right');
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}

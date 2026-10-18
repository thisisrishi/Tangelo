using System.Diagnostics;
using System.Windows.Threading;
using Tangelo.Core;

namespace Tangelo.App;

[Flags]
enum Mods { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Win = 8 }

/// Watches system-wide mouse/keyboard input. Tapping a configured modifier while a drag is in
/// progress opens a wheel under the cursor; shaking the cursor mid-drag opens a Float.
static class DragMonitor
{
    static IntPtr mouseHook, keyboardHook;
    // Delegates must stay referenced for the lifetime of the hooks.
    static Native.HookProc? mouseProc, keyboardProc;
    static Dispatcher? ui;

    static bool buttonDown, dragging;
    static Native.POINT downAt;
    static Mods pressed;
    static readonly List<(int X, long T)> shake = [];
    static long lastShake;

    public static bool Running => mouseHook != IntPtr.Zero;

    public static Mods Parse(string s)
    {
        var m = Mods.None;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<Mods>(part, true, out var v)) m |= v;
        return m;
    }

    public static string Format(Mods m) => m == Mods.None ? "None" : string.Join("+", Enum.GetValues<Mods>().Where(v => v != Mods.None && m.HasFlag(v)));

    public static void Start(Dispatcher dispatcher)
    {
        if (Running) return;
        ui = dispatcher;
        mouseProc = MouseHook;
        keyboardProc = KeyboardHook;
        var module = Native.GetModuleHandleW(Process.GetCurrentProcess().MainModule?.ModuleName);
        mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, mouseProc, module, 0);
        keyboardHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
    }

    public static void Stop()
    {
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        mouseHook = keyboardHook = IntPtr.Zero;
    }

    static unsafe IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = *(Native.MSLLHOOKSTRUCT*)lParam;
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    buttonDown = true; dragging = false; downAt = info.pt; shake.Clear();
                    break;
                case Native.WM_MOUSEMOVE when buttonDown:
                    if (!dragging && (Math.Abs(info.pt.X - downAt.X) > 8 || Math.Abs(info.pt.Y - downAt.Y) > 8)) dragging = true;
                    if (dragging) TrackShake(info.pt.X);
                    break;
                case Native.WM_LBUTTONUP:
                    var was = dragging;
                    buttonDown = dragging = false;
                    if (was) ui?.BeginInvoke(() => { WheelWindow.DragEnded(); FloatManager.DragEnded(); });
                    break;
            }
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    static unsafe IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = *(Native.KBDLLHOOKSTRUCT*)lParam;
            var mod = info.vkCode switch
            {
                0xA0 or 0xA1 or 0x10 => Mods.Shift,
                0xA2 or 0xA3 or 0x11 => Mods.Ctrl,
                0xA4 or 0xA5 or 0x12 => Mods.Alt,
                0x5B or 0x5C => Mods.Win,
                _ => Mods.None,
            };
            if (mod != Mods.None)
            {
                var msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                var before = pressed;
                pressed = down ? pressed | mod : pressed & ~mod;
                // React to the press that completes a combination (ignore auto-repeat).
                if (down && before != pressed && dragging && Native.IsDown(Native.VK_LBUTTON))
                {
                    var combo = pressed;
                    ui?.BeginInvoke(() => Tapped(combo));
                }
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    static void Tapped(Mods combo)
    {
        if (WheelWindow.IsShowing) return;
        var s = Settings.Current;
        (WheelKind Kind, string Mask, bool Enabled)[] wheels =
        [
            (WheelKind.Conversions, s.ConversionsModifier, s.ConversionsWheelEnabled),
            (WheelKind.Tools, s.ToolsModifier, s.ToolsWheelEnabled),
            (WheelKind.Actions, s.ActionsModifier, s.ActionsWheelEnabled),
        ];
        foreach (var (kind, mask, enabled) in wheels)
        {
            if (enabled && Parse(mask) == combo)
            {
                WheelWindow.ShowAtCursor(kind);
                return;
            }
        }
    }

    static void TrackShake(int x)
    {
        if (!Settings.Current.ShakeToFloat) return;
        var now = Environment.TickCount64;
        shake.Add((x, now));
        shake.RemoveAll(s => now - s.T > 600);
        if (shake.Count < 7 || now - lastShake < 1500) return;
        int reversals = 0, lastDir = 0, travel = 0;
        for (int i = 1; i < shake.Count; i++)
        {
            var dx = shake[i].X - shake[i - 1].X;
            if (Math.Abs(dx) < 3) continue;
            travel += Math.Abs(dx);
            var dir = Math.Sign(dx);
            if (lastDir != 0 && dir != lastDir) reversals++;
            lastDir = dir;
        }
        if (reversals >= 4 && travel > 300 * Native.DpiScale)
        {
            lastShake = now;
            shake.Clear();
            ui?.BeginInvoke(() => { if (!WheelWindow.IsShowing) FloatManager.NewTransientFloat(); });
        }
    }
}

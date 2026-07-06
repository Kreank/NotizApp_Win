using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NotizApp.Services;

/// <summary>
/// Globaler Hotkey Strg+Alt+N über RegisterHotKey auf einem
/// message-only HwndSource-Fenster.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1;
    const uint MOD_CONTROL = 0x2;
    const uint MOD_NOREPEAT = 0x4000;
    const uint VK_N = 0x4E;
    const int HotkeyId = 1;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    HwndSource? _source;
    bool _registriert;

    public event Action? HotkeyGedrueckt;

    /// <summary>true, wenn der Hotkey registriert werden konnte.</summary>
    public bool Aktivieren()
    {
        if (_registriert) return true;
        if (_source is null)
        {
            // HWND_MESSAGE (-3): unsichtbares message-only Fenster
            var p = new HwndSourceParameters("NotizAppHotkey")
            {
                ParentWindow = new IntPtr(-3),
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }
        _registriert = RegisterHotKey(_source.Handle, HotkeyId,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_N);
        return _registriert;
    }

    public void Deaktivieren()
    {
        if (_registriert && _source is not null)
            UnregisterHotKey(_source.Handle, HotkeyId);
        _registriert = false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyGedrueckt?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Deaktivieren();
        _source?.Dispose();
        _source = null;
    }
}

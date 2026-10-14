using System.Runtime.InteropServices;

namespace IndicOcr.App.Services;

/// <summary>
/// Global hotkey through RegisterHotKey. WM_HOTKEY is delivered to the main window's
/// message queue; a window subclass picks it up. Must be created and used on the UI thread.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4F43;
    public const string DefaultSpec = "Win+Shift+O";

    private IntPtr _hwnd;
    private bool _subclassed;
    private bool _registered;
    // Kept in a field so the delegate is not garbage collected while native code holds it.
    private readonly NativeMethods.SubclassProc _proc;

    public event Action? Pressed;

    /// <summary>The hotkey that is (or was attempted to be) registered, for display, e.g. "Win+Shift+O".</summary>
    public string Display { get; private set; } = DefaultSpec;

    /// <summary>
    /// Parses "Ctrl+Alt+Shift+Win + key" where key is A-Z, 0-9 or F1-F24. At least one modifier is required.
    /// </summary>
    public static bool TryParse(string? spec, out uint modifiers, out uint vk, out string display)
    {
        modifiers = 0; vk = 0; display = string.Empty;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var names = new List<string>();
        string? key = null;
        foreach (string raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= NativeMethods.MOD_CONTROL; names.Add("Ctrl"); break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; names.Add("Alt"); break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; names.Add("Shift"); break;
                case "win": case "windows": modifiers |= NativeMethods.MOD_WIN; names.Add("Win"); break;
                default:
                    if (key != null) return false;
                    key = raw.ToUpperInvariant();
                    break;
            }
        }
        if (modifiers == 0 || key == null) return false;

        if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
            vk = key[0]; // virtual-key codes for A-Z and 0-9 equal their ASCII values
        else if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int f) && f >= 1 && f <= 24)
            vk = (uint)(0x70 + f - 1); // VK_F1 = 0x70
        else
            return false;

        names.Add(key);
        display = string.Join("+", names);
        return true;
    }

    public HotkeyService()
    {
        _proc = WndProc;
    }

    /// <summary>Returns null on success, otherwise a short error description.</summary>
    public string? Register(IntPtr hwnd, string? spec)
    {
        _hwnd = hwnd;
        string? note = null;
        if (!TryParse(spec, out uint mods, out uint vk, out string display))
        {
            note = "The hotkey in settings.json is not valid; using " + DefaultSpec + ". ";
            TryParse(DefaultSpec, out mods, out vk, out display);
        }
        Display = display;

        if (!NativeMethods.SetWindowSubclass(hwnd, _proc, new UIntPtr(1), UIntPtr.Zero))
            return note + "Could not hook the window for hotkeys.";
        _subclassed = true;

        if (!NativeMethods.RegisterHotKey(hwnd, HotkeyId, mods | NativeMethods.MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            return note + (err == 1409
                ? display + " is already used by another program. Change \"Hotkey\" in settings.json or use the Snip button."
                : "Could not register " + display + " (error " + err + "). Use the Snip button instead.");
        }
        _registered = true;
        return note;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToUInt64() == HotkeyId)
        {
            try { Pressed?.Invoke(); } catch (Exception ex) { AppLog.Error("Hotkey handler", ex); }
            return IntPtr.Zero;
        }
        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
        if (_subclassed)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, _proc, new UIntPtr(1));
            _subclassed = false;
        }
    }
}

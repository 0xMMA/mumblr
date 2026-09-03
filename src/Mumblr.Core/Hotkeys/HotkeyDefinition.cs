namespace Mumblr.Core.Hotkeys;

/// <summary>Win32 <c>MOD_*</c> values, so a parsed hotkey can go straight into RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A hotkey as written in the config ("Ctrl+Alt+Space"), parsed into the modifier mask and the
/// virtual key code that both RegisterHotKey and the low level keyboard hook need.
/// </summary>
public readonly record struct HotkeyDefinition(HotkeyModifiers Modifiers, int VirtualKey, string Text)
{
    public override string ToString() => Text;

    public static bool TryParse(string? value, out HotkeyDefinition hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var modifiers = HotkeyModifiers.None;
        int? key = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "meta" or "cmd": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (key is not null || !TryParseKey(part, out var parsed))
                        return false;
                    key = parsed;
                    break;
            }
        }

        if (key is null)
            return false;

        hotkey = new HotkeyDefinition(modifiers, key.Value, Normalize(modifiers, parts[^1]));
        return true;
    }

    public static HotkeyDefinition Parse(string value) =>
        TryParse(value, out var hotkey) ? hotkey : throw new FormatException($"Not a valid hotkey: '{value}'");

    private static string Normalize(HotkeyModifiers modifiers, string key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(char.ToUpperInvariant(key[0]) + key[1..]);
        return string.Join('+', parts);
    }

    private static bool TryParseKey(string name, out int virtualKey)
    {
        virtualKey = 0;

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = 0x6F + functionKey; // VK_F1 == 0x70
            return true;
        }

        virtualKey = name.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "escape" or "esc" => 0x1B,
            "tab" => 0x09,
            "backspace" or "back" => 0x08,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "capslock" => 0x14,
            "pause" => 0x13,
            "scrolllock" => 0x91,
            "numlock" => 0x90,
            "printscreen" or "print" => 0x2C,
            "`" or "tilde" or "grave" => 0xC0,
            "-" or "minus" => 0xBD,
            "=" or "equals" => 0xBB,
            "[" => 0xDB,
            "]" => 0xDD,
            "\\" => 0xDC,
            ";" => 0xBA,
            "'" => 0xDE,
            "," => 0xBC,
            "." => 0xBE,
            "/" => 0xBF,
            _ => 0,
        };

        return virtualKey != 0;
    }
}

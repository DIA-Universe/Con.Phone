using System.Text.Json;
using System.Text.Json.Serialization;

using ConPhone.Core;

namespace ConPhone.Windows;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

public sealed class HotkeyBinding
{
    public int Key { get; set; } = (int)Keys.F8;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.None;

    public HotkeyBinding Clone() => new() { Key = Key, Modifiers = Modifiers };
}

public sealed class ConPhoneSettings
{
    public int MouseSpeedPercent { get; set; } = 100;
    public int ScrollSpeedPercent { get; set; } = 100;
    public bool InvertScroll { get; set; }
    public HangulKeyMode HangulKeyMode { get; set; } = HangulKeyMode.RightAlt;
    public ModifierProfile ModifierProfile { get; set; } = ModifierProfile.Standard;
    public HotkeyBinding AppleLockShortcut { get; set; } = new() { Key = (int)Keys.F12 };

    public HotkeyBinding NextDeviceHotkey { get; set; } = new() { Key = (int)Keys.F7 };
    public HotkeyBinding ToggleHotkey { get; set; } = new() { Key = (int)Keys.F8 };
    public HotkeyBinding EmergencyHotkey { get; set; } = new() { Key = (int)Keys.F9 };
    public HotkeyBinding LockAndReturnHotkey { get; set; } = new()
    {
        Key = (int)Keys.L,
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt
    };

    public int ClipboardHistoryLimit { get; set; } = 50;
    public bool CaptureClipboardText { get; set; } = true;
    public bool CaptureClipboardImages { get; set; } = false;
    public bool ClipboardSyncEnabled { get; set; } = true;
    public bool ClipboardAutoSendText { get; set; } = true;
    public bool ClipboardAutoSendImages { get; set; } = false;
    public int ClipboardBleImageLimitKb { get; set; } = 512;
    public string P2PLinkName { get; set; } = "ConPhone";
}

public static class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConPhone");

    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static ConPhoneSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ConPhoneSettings();
            return ReadJson(File.ReadAllText(FilePath));
        }
        catch
        {
            return new ConPhoneSettings();
        }
    }

    internal static ConPhoneSettings ReadJson(string json) => Normalize(
        JsonSerializer.Deserialize<ConPhoneSettings>(json, JsonOptions) ?? new ConPhoneSettings());

    public static void Save(ConPhoneSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, true);
    }

    public static ConPhoneSettings Normalize(ConPhoneSettings settings)
    {
        // 1.0 migrates even explicitly disabled legacy settings to automatic text sync.
        settings.ClipboardSyncEnabled = true;
        settings.ClipboardAutoSendText = true;
        settings.CaptureClipboardText = true;
        settings.ClipboardAutoSendImages = false;
        settings.CaptureClipboardImages = false;
        settings.MouseSpeedPercent = Math.Clamp(settings.MouseSpeedPercent, 1, 300);
        settings.ScrollSpeedPercent = Math.Clamp(settings.ScrollSpeedPercent, 25, 300);
        settings.ClipboardHistoryLimit = Math.Clamp(settings.ClipboardHistoryLimit, 10, 200);
        settings.ClipboardBleImageLimitKb = Math.Clamp(settings.ClipboardBleImageLimitKb, 64, 4096);
        settings.P2PLinkName = NormalizeP2PLinkName(settings.P2PLinkName);
        if (!Enum.IsDefined(typeof(HangulKeyMode), settings.HangulKeyMode))
            settings.HangulKeyMode = HangulKeyMode.RightAlt;
        if (!Enum.IsDefined(typeof(ModifierProfile), settings.ModifierProfile))
            settings.ModifierProfile = ModifierProfile.Standard;

        settings.NextDeviceHotkey ??= DefaultNextDevice();
        settings.ToggleHotkey ??= DefaultToggle();
        settings.EmergencyHotkey ??= DefaultEmergency();
        settings.LockAndReturnHotkey ??= DefaultLockAndReturn();
        settings.AppleLockShortcut ??= new() { Key = (int)Keys.F12 };
        if (!HotkeyCatalog.IsAllowedKey(settings.AppleLockShortcut.Key))
            settings.AppleLockShortcut = new() { Key = (int)Keys.F12 };

        if (!HotkeyCatalog.IsAllowedKey(settings.NextDeviceHotkey.Key)) settings.NextDeviceHotkey = DefaultNextDevice();
        if (!HotkeyCatalog.IsAllowedKey(settings.ToggleHotkey.Key)) settings.ToggleHotkey = DefaultToggle();
        if (!HotkeyCatalog.IsAllowedKey(settings.EmergencyHotkey.Key)) settings.EmergencyHotkey = DefaultEmergency();
        if (!HotkeyCatalog.IsAllowedKey(settings.LockAndReturnHotkey.Key)) settings.LockAndReturnHotkey = DefaultLockAndReturn();

        var hotkeys = new[]
        {
            settings.NextDeviceHotkey,
            settings.ToggleHotkey,
            settings.EmergencyHotkey,
            settings.LockAndReturnHotkey
        };

        var duplicate = hotkeys
            .Select((binding, index) => new { binding, index })
            .Any(x => hotkeys
                .Take(x.index)
                .Any(previous => HotkeyCatalog.Same(previous, x.binding)));

        if (duplicate)
        {
            settings.NextDeviceHotkey = DefaultNextDevice();
            settings.ToggleHotkey = DefaultToggle();
            settings.EmergencyHotkey = DefaultEmergency();
            settings.LockAndReturnHotkey = DefaultLockAndReturn();
        }

        return settings;
    }

    private static string NormalizeP2PLinkName(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "ConPhone" : value.Trim();
        var safe = new string(text
            .Where(ch => (ch is >= 'A' and <= 'Z') || (ch is >= 'a' and <= 'z') || (ch is >= '0' and <= '9') || ch is '-' or '_' or '.')
            .Take(18)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "ConPhone" : safe;
    }

    public static HotkeyBinding DefaultNextDevice() => new() { Key = (int)Keys.F7 };
    public static HotkeyBinding DefaultToggle() => new() { Key = (int)Keys.F8 };
    public static HotkeyBinding DefaultEmergency() => new() { Key = (int)Keys.F9 };
    public static HotkeyBinding DefaultLockAndReturn() => new()
    {
        Key = (int)Keys.L,
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt
    };
}

public static class HotkeyCatalog
{
    public static bool IsModifierKey(int key) => key is
        (int)Keys.ControlKey or (int)Keys.LControlKey or (int)Keys.RControlKey or
        (int)Keys.Menu or (int)Keys.LMenu or (int)Keys.RMenu or
        (int)Keys.ShiftKey or (int)Keys.LShiftKey or (int)Keys.RShiftKey or
        (int)Keys.LWin or (int)Keys.RWin;

    public static bool IsAllowedKey(int key)
    {
        if (IsModifierKey(key)) return false;
        if (key < 0x08 || key > 0xFE) return false;
        return key is not (int)Keys.ProcessKey and not (int)Keys.Packet;
    }

    public static bool Same(HotkeyBinding a, HotkeyBinding b) =>
        a.Key == b.Key && a.Modifiers == b.Modifiers;

    public static string Display(HotkeyBinding binding)
    {
        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(NameOf(binding.Key));
        return string.Join("+", parts);
    }

    public static string DisplayModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "키 조합을 누르세요" : string.Join("+", parts) + "+…";
    }

    public static string NameOf(int key)
    {
        if (key is >= (int)Keys.A and <= (int)Keys.Z) return ((char)key).ToString();
        if (key is >= (int)Keys.D0 and <= (int)Keys.D9) return ((char)key).ToString();
        if (key is >= (int)Keys.F1 and <= (int)Keys.F24) return $"F{key - (int)Keys.F1 + 1}";

        return key switch
        {
            (int)Keys.Space => "Space",
            (int)Keys.Tab => "Tab",
            (int)Keys.Enter => "Enter",
            (int)Keys.Escape => "Esc",
            (int)Keys.Insert => "Insert",
            (int)Keys.Delete => "Delete",
            (int)Keys.Home => "Home",
            (int)Keys.End => "End",
            (int)Keys.PageUp => "Page Up",
            (int)Keys.PageDown => "Page Down",
            (int)Keys.Up => "↑",
            (int)Keys.Down => "↓",
            (int)Keys.Left => "←",
            (int)Keys.Right => "→",
            (int)Keys.Pause => "Pause",
            (int)Keys.Scroll => "Scroll Lock",
            (int)Keys.Oemtilde => "`",
            (int)Keys.OemMinus => "-",
            (int)Keys.Oemplus => "=",
            (int)Keys.OemOpenBrackets => "[",
            (int)Keys.Oem6 => "]",
            (int)Keys.Oem5 => "\\",
            (int)Keys.Oem1 => ";",
            (int)Keys.Oem7 => "'",
            (int)Keys.Oemcomma => ",",
            (int)Keys.OemPeriod => ".",
            (int)Keys.OemQuestion => "/",
            _ => new KeysConverter().ConvertToString((Keys)key) ?? $"VK 0x{key:X2}"
        };
    }
}

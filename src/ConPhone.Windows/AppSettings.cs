using System.Text.Json;
using System.Text.Json.Serialization;

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

public enum TargetProfile
{
    Android = 0,
    IPhoneIPad = 1,
    Mac = 2
}

public enum SpecialKeyAction
{
    None = 0,
    PlayPause,
    FastForward,
    Rewind,
    NextTrack,
    PreviousTrack,
    Stop,
    Eject,
    Mute,
    VolumeUp,
    VolumeDown,
    Search,
    Home,
    Back,
    PowerPlus
}

public sealed class SpecialKeyBinding
{
    public int SourceKey { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SpecialKeyAction Action { get; set; }
}

public static class SpecialKeyCatalog
{
    public static List<SpecialKeyBinding> Defaults() =>
        Enumerable.Range((int)Keys.F1, 12)
            .Select(key => new SpecialKeyBinding { SourceKey = key, Action = SpecialKeyAction.None })
            .ToList();

    public static string Display(SpecialKeyAction action) => action switch
    {
        SpecialKeyAction.None => "없음",
        SpecialKeyAction.PlayPause => "재생 / 일시정지",
        SpecialKeyAction.FastForward => "빨리 감기",
        SpecialKeyAction.Rewind => "되감기",
        SpecialKeyAction.NextTrack => "다음 트랙",
        SpecialKeyAction.PreviousTrack => "이전 트랙",
        SpecialKeyAction.Stop => "정지",
        SpecialKeyAction.Eject => "Eject / 화면 키보드",
        SpecialKeyAction.Mute => "음소거",
        SpecialKeyAction.VolumeUp => "볼륨 올림",
        SpecialKeyAction.VolumeDown => "볼륨 내림",
        SpecialKeyAction.Search => "검색",
        SpecialKeyAction.Home => "Home",
        SpecialKeyAction.Back => "Back",
        SpecialKeyAction.PowerPlus => "Power+ / 잠금 후 PC 복귀",
        _ => action.ToString()
    };

    public static ushort ConsumerUsage(SpecialKeyAction action) => action switch
    {
        SpecialKeyAction.PlayPause => 0x00CD,
        SpecialKeyAction.FastForward => 0x00B3,
        SpecialKeyAction.Rewind => 0x00B4,
        SpecialKeyAction.NextTrack => 0x00B5,
        SpecialKeyAction.PreviousTrack => 0x00B6,
        SpecialKeyAction.Stop => 0x00B7,
        SpecialKeyAction.Eject => 0x00B8,
        SpecialKeyAction.Mute => 0x00E2,
        SpecialKeyAction.VolumeUp => 0x00E9,
        SpecialKeyAction.VolumeDown => 0x00EA,
        SpecialKeyAction.Search => 0x0221,
        SpecialKeyAction.Home => 0x0223,
        SpecialKeyAction.Back => 0x0224,
        _ => 0
    };
}

public sealed class ConPhoneSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TargetProfile TargetProfile { get; set; } = TargetProfile.Android;
    public bool AppleSwapCommandOption { get; set; }
    public List<SpecialKeyBinding> SpecialKeys { get; set; } = SpecialKeyCatalog.Defaults();

    public int MouseSpeedPercent { get; set; } = 100;
    public int ScrollSpeedPercent { get; set; } = 100;
    public bool InvertScroll { get; set; }

    public HotkeyBinding ToggleHotkey { get; set; } = new() { Key = (int)Keys.F8 };
    public HotkeyBinding EmergencyHotkey { get; set; } = new() { Key = (int)Keys.F9 };
    public HotkeyBinding LockAndReturnHotkey { get; set; } = new()
    {
        Key = (int)Keys.L,
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt
    };

    public int ClipboardHistoryLimit { get; set; } = 50;
    public bool CaptureClipboardText { get; set; } = true;
    public bool CaptureClipboardImages { get; set; } = true;
}

public static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConPhone");

    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static ConPhoneSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ConPhoneSettings();
            var settings = JsonSerializer.Deserialize<ConPhoneSettings>(File.ReadAllText(FilePath));
            return Normalize(settings ?? new ConPhoneSettings());
        }
        catch
        {
            return new ConPhoneSettings();
        }
    }

    public static void Save(ConPhoneSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, true);
    }

    public static ConPhoneSettings Normalize(ConPhoneSettings settings)
    {
        settings.MouseSpeedPercent = Math.Clamp(settings.MouseSpeedPercent, 25, 300);
        settings.ScrollSpeedPercent = Math.Clamp(settings.ScrollSpeedPercent, 25, 300);
        settings.ClipboardHistoryLimit = Math.Clamp(settings.ClipboardHistoryLimit, 10, 200);

        var configuredSpecialKeys = settings.SpecialKeys ?? [];
        settings.SpecialKeys = SpecialKeyCatalog.Defaults()
            .Select(defaultBinding =>
            {
                var configured = configuredSpecialKeys.FirstOrDefault(x =>
                    x.SourceKey == defaultBinding.SourceKey &&
                    Enum.IsDefined(x.Action));
                return configured is null
                    ? defaultBinding
                    : new SpecialKeyBinding
                    {
                        SourceKey = configured.SourceKey,
                        Action = configured.Action
                    };
            })
            .ToList();

        settings.ToggleHotkey ??= DefaultToggle();
        settings.EmergencyHotkey ??= DefaultEmergency();
        settings.LockAndReturnHotkey ??= DefaultLockAndReturn();

        if (!HotkeyCatalog.IsAllowedKey(settings.ToggleHotkey.Key)) settings.ToggleHotkey = DefaultToggle();
        if (!HotkeyCatalog.IsAllowedKey(settings.EmergencyHotkey.Key)) settings.EmergencyHotkey = DefaultEmergency();
        if (!HotkeyCatalog.IsAllowedKey(settings.LockAndReturnHotkey.Key)) settings.LockAndReturnHotkey = DefaultLockAndReturn();

        if (HotkeyCatalog.Same(settings.ToggleHotkey, settings.EmergencyHotkey) ||
            HotkeyCatalog.Same(settings.ToggleHotkey, settings.LockAndReturnHotkey) ||
            HotkeyCatalog.Same(settings.EmergencyHotkey, settings.LockAndReturnHotkey))
        {
            settings.ToggleHotkey = DefaultToggle();
            settings.EmergencyHotkey = DefaultEmergency();
            settings.LockAndReturnHotkey = DefaultLockAndReturn();
        }

        return settings;
    }

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

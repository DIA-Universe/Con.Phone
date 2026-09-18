using System.Text.Json;
using System.Text.Json.Serialization;
using ConPhone.Core;
namespace ConPhone.Windows;

[Flags] public enum HotkeyModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }
public sealed class HotkeyBinding
{
    public int Key { get; set; } = 0x77;
    [JsonConverter(typeof(JsonStringEnumConverter))] public HotkeyModifiers Modifiers { get; set; }
    [JsonIgnore] public KeyChord Chord => new(Key, (ChordModifiers)Modifiers);
    public HotkeyBinding Clone() => new() { Key = Key, Modifiers = Modifiers };
    public static HotkeyBinding From(KeyChord value) => new() { Key = value.Key, Modifiers = (HotkeyModifiers)value.Modifiers };
}
public sealed class ConPhoneSettings
{
    public int MouseSpeedPercent { get; set; } = 100;
    public int ScrollSpeedPercent { get; set; } = 100;
    public bool InvertScroll { get; set; }
    public int MouseReportIntervalMs { get; set; } = 16;
    public HotkeyBinding ToggleHotkey { get; set; } = new() { Key = 0x77 };
    public HotkeyBinding EmergencyHotkey { get; set; } = new() { Key = 0x78 };
    public HotkeyBinding LockAndReturnHotkey { get; set; } = new() { Key = 0x4C, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt };
    public bool EnablePhoneLock { get; set; } // Explicit opt-in; Android Meta+L compatibility test, not guaranteed lock.
    public int ClipboardHistoryLimit { get; set; } = 50;
    public bool CaptureClipboardText { get; set; } = true;
    public bool CaptureClipboardImages { get; set; } = true;
    public bool PersistClipboardHistory { get; set; } // Session-only unless explicitly enabled.
    public ConPhoneSettings Clone() => JsonSerializer.Deserialize<ConPhoneSettings>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        var router = new KeyboardRouter();
        router.Configure(ToggleHotkey.Chord, EmergencyHotkey.Chord, LockAndReturnHotkey.Chord, EnablePhoneLock);
        if (MouseSpeedPercent is <25 or >300 || ScrollSpeedPercent is <25 or >300 || MouseReportIntervalMs is <8 or >50 || ClipboardHistoryLimit is <10 or >200)
            throw new ArgumentException("설정 범위를 확인하세요.");
    }
}
public static class SettingsStore
{
    public static string Root => Environment.GetEnvironmentVariable("CONPHONE_TEST_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConPhone");
    public static string FilePath => Path.Combine(Root, "settings.json");
    public static string Notice { get; private set; } = "";
    public static ConPhoneSettings Load()
    {
        Notice = "";
        try
        {
            var s = File.Exists(FilePath) ? JsonSerializer.Deserialize<ConPhoneSettings>(File.ReadAllText(FilePath)) ?? new() : new ConPhoneSettings();
            s.MouseSpeedPercent = Math.Clamp(s.MouseSpeedPercent,25,300);
            s.ScrollSpeedPercent = Math.Clamp(s.ScrollSpeedPercent,25,300);
            s.MouseReportIntervalMs = Math.Clamp(s.MouseReportIntervalMs,8,50);
            s.ClipboardHistoryLimit = Math.Clamp(s.ClipboardHistoryLimit,10,200);
            s.ToggleHotkey ??= new() { Key=0x77 }; s.EmergencyHotkey ??= new() { Key=0x78 };
            s.LockAndReturnHotkey ??= new() { Key=0x4C, Modifiers=HotkeyModifiers.Control|HotkeyModifiers.Alt };
            s.Validate(); return s;
        }
        catch (Exception ex) { Notice = $"기존 설정을 읽지 못해 기본값을 사용합니다 ({ex.GetType().Name}). 원본 파일은 유지했습니다."; return new(); }
    }
    public static void Save(ConPhoneSettings settings)
    {
        settings.Validate(); Directory.CreateDirectory(Root);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings,new JsonSerializerOptions { WriteIndented=true }));
        File.Move(temp, FilePath, true);
    }
}
public static class HotkeyCatalog
{
    public static bool IsModifierKey(int key) => KeyboardRouter.IsModifier(key);
    public static bool IsAllowedKey(int key) => key >= 8 && key <= 0xFE && !IsModifierKey(key) && key is not 0xE5 and not 0xE7;
    public static bool Same(HotkeyBinding a, HotkeyBinding b) => a.Chord == b.Chord;
    public static string Display(HotkeyBinding binding)
    {
        List<string> p = [];
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) p.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) p.Add("Alt");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) p.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) p.Add("Win");
        p.Add(binding.Key switch { >=0x41 and <=0x5A => ((char)binding.Key).ToString(), >=0x70 and <=0x87 => $"F{binding.Key-0x6F}", _ => new KeysConverter().ConvertToString((Keys)binding.Key) ?? $"0x{binding.Key:X2}" });
        return string.Join(" + ",p);
    }
}

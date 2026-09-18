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
    public int Key { get; set; } = 0x77; // F8
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.None;

    public HotkeyBinding Clone() => new() { Key = Key, Modifiers = Modifiers };
}

public sealed class ConPhoneSettings
{
    public int MouseSpeedPercent { get; set; } = 100;
    public int ScrollSpeedPercent { get; set; } = 100;
    public bool InvertScroll { get; set; }
    public HotkeyBinding ToggleHotkey { get; set; } = new() { Key = 0x77 }; // F8
    public HotkeyBinding EmergencyHotkey { get; set; } = new() { Key = 0x78 }; // F9
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
        settings.ToggleHotkey ??= new HotkeyBinding { Key = 0x77 };
        settings.EmergencyHotkey ??= new HotkeyBinding { Key = 0x78 };
        if (!HotkeyCatalog.IsAllowedKey(settings.ToggleHotkey.Key)) settings.ToggleHotkey.Key = 0x77;
        if (!HotkeyCatalog.IsAllowedKey(settings.EmergencyHotkey.Key)) settings.EmergencyHotkey.Key = 0x78;
        return settings;
    }
}

public sealed record HotkeyOption(string Name, int Key)
{
    public override string ToString() => Name;
}

public static class HotkeyCatalog
{
    public static IReadOnlyList<HotkeyOption> Options { get; } =
    [
        new("F1", 0x70), new("F2", 0x71), new("F3", 0x72), new("F4", 0x73),
        new("F5", 0x74), new("F6", 0x75), new("F7", 0x76), new("F8", 0x77),
        new("F9", 0x78), new("F10", 0x79), new("F11", 0x7A), new("F12", 0x7B),
        new("Space", 0x20), new("↑", 0x26), new("↓", 0x28), new("←", 0x25), new("→", 0x27),
        new("Insert", 0x2D), new("Home", 0x24), new("End", 0x23),
        new("Page Up", 0x21), new("Page Down", 0x22), new("Pause", 0x13), new("Scroll Lock", 0x91)
    ];

    public static bool IsAllowedKey(int key) => Options.Any(x => x.Key == key);
    public static string NameOf(int key) => Options.FirstOrDefault(x => x.Key == key)?.Name ?? $"VK 0x{key:X2}";
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
}

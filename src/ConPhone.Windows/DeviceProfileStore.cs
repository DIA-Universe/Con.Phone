using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConPhone.Core;

namespace ConPhone.Windows;

public sealed class DeviceInputProfile
{
    public HangulKeyMode HangulKeyMode { get; set; } = HangulKeyMode.RightAlt;
    public ModifierProfile ModifierProfile { get; set; } = ModifierProfile.Standard;
}

public static class DeviceProfileStore
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConPhone");
    private static readonly string FilePath = Path.Combine(
        DirectoryPath,
        "device-profiles.json");

    public static DeviceInputProfile Get(
        string deviceId,
        HangulKeyMode fallbackHangul,
        ModifierProfile fallbackModifier)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return new DeviceInputProfile
            {
                HangulKeyMode = fallbackHangul,
                ModifierProfile = fallbackModifier
            };

        lock (Gate)
        {
            var profiles = LoadUnsafe();
            if (profiles.TryGetValue(Key(deviceId), out var profile))
                return Normalize(profile);

            return new DeviceInputProfile
            {
                HangulKeyMode = fallbackHangul,
                ModifierProfile = fallbackModifier
            };
        }
    }

    public static void Save(
        string deviceId,
        HangulKeyMode hangulKeyMode,
        ModifierProfile modifierProfile)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        lock (Gate)
        {
            var profiles = LoadUnsafe();
            var profile = profiles.GetValueOrDefault(Key(deviceId)) ?? new DeviceInputProfile();
            profile.HangulKeyMode = hangulKeyMode;
            profile.ModifierProfile = modifierProfile;
            profiles[Key(deviceId)] = Normalize(profile);
            SaveUnsafe(profiles);
        }
    }

    public static void Remove(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        lock (Gate)
        {
            var profiles = LoadUnsafe();
            if (profiles.Remove(Key(deviceId)))
                SaveUnsafe(profiles);
        }
    }

    private static string Key(string deviceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));

    private static DeviceInputProfile Normalize(DeviceInputProfile profile)
    {
        if (!Enum.IsDefined(typeof(HangulKeyMode), profile.HangulKeyMode))
            profile.HangulKeyMode = HangulKeyMode.RightAlt;
        if (!Enum.IsDefined(typeof(ModifierProfile), profile.ModifierProfile))
            profile.ModifierProfile = ModifierProfile.Standard;
        return profile;
    }

    private static Dictionary<string, DeviceInputProfile> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, DeviceInputProfile>(StringComparer.Ordinal);

            var parsed = JsonSerializer.Deserialize<Dictionary<string, DeviceInputProfile>>(
                File.ReadAllText(FilePath));

            return parsed is null
                ? new Dictionary<string, DeviceInputProfile>(StringComparer.Ordinal)
                : new Dictionary<string, DeviceInputProfile>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, DeviceInputProfile>(StringComparer.Ordinal);
        }
    }

    private static void SaveUnsafe(Dictionary<string, DeviceInputProfile> profiles)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                profiles,
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}

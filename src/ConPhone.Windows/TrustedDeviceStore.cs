using Windows.Security.Credentials;

namespace ConPhone.Windows;

public static class TrustedDeviceStore
{
    private const string DevicesResource = "ConPhone.TrustedDevice";
    private const string ActiveResource = "ConPhone.TrustedDevice.Active";
    private const string ActiveUser = "active";

    public const int MaxDevices = 5;

    public static string? Load() => LoadActive();

    public static string? LoadActive()
    {
        try
        {
            var vault = new PasswordVault();
            var active = vault.Retrieve(ActiveResource, ActiveUser);
            active.RetrievePassword();
            if (!string.IsNullOrWhiteSpace(active.Password) &&
                Contains(active.Password))
                return active.Password;
        }
        catch
        {
        }

        // Backward compatibility with the original single-device "primary" entry.
        var legacy = LoadAll().FirstOrDefault();
        if (legacy is not null)
            SetActive(legacy);
        return legacy;
    }

    public static IReadOnlyList<string> LoadAll()
    {
        var items = new List<(int Slot, string Id)>();
        var vault = new PasswordVault();

        try
        {
            foreach (var credential in vault.FindAllByResource(DevicesResource))
            {
                try
                {
                    credential.RetrievePassword();
                    if (string.IsNullOrWhiteSpace(credential.Password))
                        continue;

                    var slot = ParseSlot(credential.UserName);
                    items.Add((slot, credential.Password));
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return items
            .OrderBy(x => x.Slot)
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDevices)
            .ToArray();
    }

    public static bool Contains(string deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        LoadAll().Contains(deviceId, StringComparer.Ordinal);

    public static int Remember(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device id is required.", nameof(deviceId));

        var existing = LoadAll();
        var existingIndex = Array.FindIndex(existing.ToArray(),
            x => string.Equals(x, deviceId, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            SetActive(deviceId);
            return existingIndex + 1;
        }

        if (existing.Count >= MaxDevices)
            throw new InvalidOperationException(
                $"신뢰 기기는 최대 {MaxDevices}대까지 등록할 수 있습니다. 사용하지 않는 기기를 먼저 해제하세요.");

        var usedSlots = ExistingSlotNumbers();
        var slot = Enumerable.Range(1, MaxDevices)
            .First(x => !usedSlots.Contains(x));

        var vault = new PasswordVault();
        vault.Add(new PasswordCredential(
            DevicesResource,
            $"slot-{slot}",
            deviceId));

        SetActive(deviceId);
        return slot;
    }

    // Compatibility for callers that used the old single-device API.
    public static void Save(string deviceId) => Remember(deviceId);

    public static void SetActive(string deviceId)
    {
        if (!Contains(deviceId))
            throw new InvalidOperationException("등록되지 않은 기기는 활성 대상으로 선택할 수 없습니다.");

        var vault = new PasswordVault();
        RemoveCredentials(vault, ActiveResource, _ => true);
        vault.Add(new PasswordCredential(ActiveResource, ActiveUser, deviceId));
    }

    public static bool Remove(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        var removed = false;
        var vault = new PasswordVault();
        try
        {
            foreach (var credential in vault.FindAllByResource(DevicesResource).ToArray())
            {
                try
                {
                    credential.RetrievePassword();
                    if (!string.Equals(credential.Password, deviceId, StringComparison.Ordinal))
                        continue;

                    vault.Remove(credential);
                    removed = true;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        if (string.Equals(ReadActiveRaw(), deviceId, StringComparison.Ordinal))
        {
            RemoveCredentials(vault, ActiveResource, _ => true);
            var next = LoadAll().FirstOrDefault();
            if (next is not null) SetActive(next);
        }

        return removed;
    }

    public static void Clear()
    {
        var vault = new PasswordVault();
        RemoveCredentials(vault, DevicesResource, _ => true);
        RemoveCredentials(vault, ActiveResource, _ => true);
    }

    private static string? ReadActiveRaw()
    {
        try
        {
            var vault = new PasswordVault();
            var active = vault.Retrieve(ActiveResource, ActiveUser);
            active.RetrievePassword();
            return active.Password;
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<int> ExistingSlotNumbers()
    {
        var result = new HashSet<int>();
        var vault = new PasswordVault();
        try
        {
            foreach (var credential in vault.FindAllByResource(DevicesResource))
            {
                var slot = ParseSlot(credential.UserName);
                if (slot is >= 1 and <= MaxDevices)
                    result.Add(slot);
            }
        }
        catch
        {
        }
        return result;
    }

    private static int ParseSlot(string userName)
    {
        if (userName.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (userName.StartsWith("slot-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(userName.AsSpan(5), out var slot) &&
            slot is >= 1 and <= MaxDevices)
            return slot;

        return MaxDevices + 1;
    }

    private static void RemoveCredentials(
        PasswordVault vault,
        string resource,
        Func<PasswordCredential, bool> predicate)
    {
        try
        {
            foreach (var credential in vault.FindAllByResource(resource).ToArray())
                if (predicate(credential))
                    vault.Remove(credential);
        }
        catch
        {
        }
    }
}

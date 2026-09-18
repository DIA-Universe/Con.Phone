using Windows.Security.Credentials;

namespace ConPhone.Windows;

public static class TrustedDeviceStore
{
    private const string Resource = "ConPhone.TrustedDevice";
    private const string User = "primary";

    public static string? Load()
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(Resource, User);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password)
                ? null
                : credential.Password;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device id is required.", nameof(deviceId));

        var vault = new PasswordVault();
        try
        {
            foreach (var old in vault.FindAllByResource(Resource)
                         .Where(x => x.UserName == User)
                         .ToArray())
                vault.Remove(old);
        }
        catch
        {
        }

        vault.Add(new PasswordCredential(Resource, User, deviceId));
    }

    public static void Clear()
    {
        var vault = new PasswordVault();
        try
        {
            foreach (var old in vault.FindAllByResource(Resource)
                         .Where(x => x.UserName == User)
                         .ToArray())
                vault.Remove(old);
        }
        catch
        {
        }
    }
}

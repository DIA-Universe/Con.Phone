using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;

namespace ConPhone.Windows;

public sealed record DirectLinkConfig(
    string Ssid,
    string Passphrase,
    string? HostAddress = null)
{
    public byte[] Encode()
    {
        var ssid = Encoding.UTF8.GetBytes(Ssid);
        var pass = Encoding.UTF8.GetBytes(Passphrase);
        var host = string.IsNullOrWhiteSpace(HostAddress)
            ? Array.Empty<byte>()
            : Encoding.ASCII.GetBytes(HostAddress);

        if (ssid.Length is < 1 or > 32 ||
            pass.Length is < 8 or > 63 ||
            host.Length > 45)
            throw new InvalidOperationException("Invalid Wi-Fi Direct credentials.");

        var result = new byte[4 + ssid.Length + pass.Length + host.Length];
        result[0] = 2;
        result[1] = (byte)ssid.Length;
        result[2] = (byte)pass.Length;
        result[3] = (byte)host.Length;
        ssid.CopyTo(result, 4);
        pass.CopyTo(result, 4 + ssid.Length);
        host.CopyTo(result, 4 + ssid.Length + pass.Length);
        return result;
    }
}

public static class DirectLinkCredentialStore
{
    private const string Resource = "ConPhone.DirectLink";

    public static DirectLinkConfig GetOrCreate()
    {
        var vault = new PasswordVault();
        try
        {
            var existing = vault.FindAllByResource(Resource).FirstOrDefault();
            if (existing is not null)
            {
                existing.RetrievePassword();
                if (!string.IsNullOrWhiteSpace(existing.UserName) &&
                    !string.IsNullOrWhiteSpace(existing.Password))
                    return new DirectLinkConfig(existing.UserName, existing.Password);
            }
        }
        catch (Exception)
        {
        }

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        var ssid = $"DIRECT-CP-{suffix}";
        var passphrase = DirectClipboardLink.CreatePassphraseValue();

        try
        {
            vault.Add(new PasswordCredential(Resource, ssid, passphrase));
        }
        catch (Exception)
        {
            // If Password Vault is unavailable, this session still gets an ephemeral direct link.
        }

        return new DirectLinkConfig(ssid, passphrase);
    }
}

public sealed class DirectClipboardLink : IDisposable
{
    private WiFiDirectAdvertisementPublisher? _publisher;
    private DirectLinkConfig? _config;
    private HashSet<string> _baselineIpv4 = [];
    private bool _disposed;

    public event Action<string>? Status;

    public DirectLinkConfig? Config =>
        !Running || _config is null
            ? null
            : _config with { HostAddress = FindDirectIpv4() };
    public bool Running => _publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_publisher is not null)
        {
            if (_publisher.Status == WiFiDirectAdvertisementPublisherStatus.Stopped)
                _publisher.Start();
            return;
        }

        _baselineIpv4 = CurrentIpv4();
        var config = DirectLinkCredentialStore.GetOrCreate();

        var publisher = new WiFiDirectAdvertisementPublisher();
        publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
        publisher.Advertisement.ListenStateDiscoverability =
            WiFiDirectAdvertisementListenStateDiscoverability.Intensive;

        var legacy = publisher.Advertisement.LegacySettings;
        legacy.IsEnabled = true;
        legacy.Ssid = config.Ssid;
        legacy.Passphrase = new PasswordCredential("ConPhone.Direct", config.Ssid, config.Passphrase);

        publisher.StatusChanged += (_, args) =>
        {
            var suffixText = args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted
                ? $" · 오류:{args.Error}"
                : "";
            Status?.Invoke($"Clipboard P2P AP: {args.Status}{suffixText}");
        };

        _config = config;
        _publisher = publisher;

        try
        {
            publisher.Start();
            Status?.Invoke("Clipboard P2P 고속 링크 시작.");
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard P2P 시작 불가; BLE fallback 유지: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        var publisher = _publisher;
        _publisher = null;
        _config = null;

        if (publisher is null) return;
        try
        {
            if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started)
                publisher.Stop();
        }
        catch
        {
        }
    }

    private string? FindDirectIpv4()
    {
        var directAdapter = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                (nic.Name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                 nic.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(nic =>
            {
                try { return nic.GetIPProperties().UnicastAddresses; }
                catch (NetworkInformationException) { return []; }
            })
            .Select(x => x.Address)
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal));

        if (directAdapter is not null)
            return directAdapter.ToString();

        var current = CurrentIpv4();
        var created = current
            .Where(address => !_baselineIpv4.Contains(address))
            .OrderByDescending(address => address.StartsWith("192.168.", StringComparison.Ordinal))
            .ThenByDescending(address => address.StartsWith("172.", StringComparison.Ordinal))
            .FirstOrDefault();

        if (created is not null) return created;

        // Windows Wi-Fi Direct legacy GO commonly uses an ICS private address.
        // Only expose it if it is actually assigned on this machine.
        return current.FirstOrDefault(address => address.StartsWith("192.168.137.", StringComparison.Ordinal));
    }

    private static HashSet<string> CurrentIpv4()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            try
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address))
                        continue;

                    var value = unicast.Address.ToString();
                    if (!value.StartsWith("169.254.", StringComparison.Ordinal))
                        result.Add(value);
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return result;
    }

    internal static string CreatePassphraseValue()
    {
        // WPA2 personal passphrase: ASCII 8-63 characters.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(20);
        var chars = new char[20];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

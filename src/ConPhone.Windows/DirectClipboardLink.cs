using System.Security.Cryptography;
using System.Text;
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;

namespace ConPhone.Windows;

public sealed record DirectLinkConfig(string Ssid, string Passphrase)
{
    public byte[] Encode()
    {
        var ssid = Encoding.UTF8.GetBytes(Ssid);
        var pass = Encoding.UTF8.GetBytes(Passphrase);
        if (ssid.Length is < 1 or > 32 || pass.Length is < 8 or > 63)
            throw new InvalidOperationException("Invalid Wi-Fi Direct credentials.");

        var result = new byte[3 + ssid.Length + pass.Length];
        result[0] = 1;
        result[1] = (byte)ssid.Length;
        result[2] = (byte)pass.Length;
        ssid.CopyTo(result, 3);
        pass.CopyTo(result, 3 + ssid.Length);
        return result;
    }
}

public sealed class DirectClipboardLink : IDisposable
{
    private WiFiDirectAdvertisementPublisher? _publisher;
    private DirectLinkConfig? _config;
    private bool _disposed;

    public event Action<string>? Status;

    public DirectLinkConfig? Config => _config;
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

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        var passphrase = CreatePassphrase();
        var config = new DirectLinkConfig($"DIRECT-CP-{suffix}", passphrase);

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
            Status?.Invoke("Clipboard P2P 고속 링크 광고 시작.");
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard P2P 시작 불가; LAN/BLE fallback 유지: {ex.Message}");
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

    private static string CreatePassphrase()
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

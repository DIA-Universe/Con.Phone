using System.Security.Cryptography;

namespace ConPhone.Core;

public static class P2PSecurity
{
    public static ReadOnlySpan<byte> HelloMagic => "CPHELLO2"u8;
    public static ReadOnlySpan<byte> ReadyMagic => "CPREADY2"u8;
    public static ReadOnlySpan<byte> AadPrefix => "ConPhone-Clipboard-P2P-v2"u8;
    public static ReadOnlySpan<byte> SessionLabel => "ConPhone-P2P-Session-v2"u8;

    public static byte[] DeriveSessionKey(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> deviceHash,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce)
    {
        if (secret.Length != 32 ||
            deviceHash.Length != 32 ||
            clientNonce.Length != 16 ||
            serverNonce.Length != 16)
            throw new ArgumentException("Invalid P2P session-key input length.");

        var material = new byte[
            SessionLabel.Length +
            deviceHash.Length +
            clientNonce.Length +
            serverNonce.Length];

        var offset = 0;
        SessionLabel.CopyTo(material.AsSpan(offset));
        offset += SessionLabel.Length;
        deviceHash.CopyTo(material.AsSpan(offset));
        offset += deviceHash.Length;
        clientNonce.CopyTo(material.AsSpan(offset));
        offset += clientNonce.Length;
        serverNonce.CopyTo(material.AsSpan(offset));

        try
        {
            return HMACSHA256.HashData(secret, material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }
}

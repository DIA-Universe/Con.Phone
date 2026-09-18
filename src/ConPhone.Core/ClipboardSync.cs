using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ConPhone.Core;

public enum ClipboardContentKind : byte
{
    TextUtf8 = 1,
    Png = 2,
    Jpeg = 3,
    Webp = 4
}

public sealed record ClipboardContent(ClipboardContentKind Kind, byte[] Data, string Hash)
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxImageBytes = 32 * 1024 * 1024;

    public static ClipboardContent FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        return Create(ClipboardContentKind.TextUtf8, bytes);
    }

    public static ClipboardContent FromImage(ClipboardContentKind kind, byte[] bytes)
    {
        if (kind == ClipboardContentKind.TextUtf8)
            throw new ArgumentException("Image content kind is required.", nameof(kind));
        return Create(kind, bytes);
    }

    public static ClipboardContent Create(ClipboardContentKind kind, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Validate(kind, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new ClipboardContent(kind, bytes.ToArray(), hash);
    }

    public string GetText()
    {
        if (Kind != ClipboardContentKind.TextUtf8)
            throw new InvalidOperationException("Clipboard content is not text.");
        return new UTF8Encoding(false, true).GetString(Data);
    }

    public static void Validate(ClipboardContentKind kind, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            throw new ArgumentException("Clipboard content must not be empty.");

        var max = kind == ClipboardContentKind.TextUtf8 ? MaxTextBytes : MaxImageBytes;
        if (bytes.Length > max)
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Clipboard payload exceeds {max} bytes.");

        switch (kind)
        {
            case ClipboardContentKind.TextUtf8:
                _ = new UTF8Encoding(false, true).GetString(bytes);
                break;
            case ClipboardContentKind.Png:
                if (bytes.Length < 8 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new ArgumentException("Invalid PNG signature.");
                break;
            case ClipboardContentKind.Jpeg:
                if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 ||
                    bytes[^2] != 0xFF || bytes[^1] != 0xD9)
                    throw new ArgumentException("Invalid JPEG signature.");
                break;
            case ClipboardContentKind.Webp:
                if (bytes.Length < 12 ||
                    !bytes[..4].SequenceEqual("RIFF"u8) ||
                    !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
                    throw new ArgumentException("Invalid WebP signature.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}

public sealed record ClipboardChunk(
    Guid TransferId,
    ClipboardContentKind Kind,
    int Index,
    int Count,
    int TotalLength,
    byte[] Hash,
    byte[] Payload);

public static class ClipboardWireCodec
{
    private static readonly byte[] Magic = "CPH1"u8.ToArray();
    public const int DefaultChunkSize = 64 * 1024;

    public static IReadOnlyList<byte[]> Encode(ClipboardContent content, int chunkSize = DefaultChunkSize)
    {
        if (chunkSize is < 256 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        ClipboardContent.Validate(content.Kind, content.Data);
        var transfer = Guid.NewGuid();
        var hash = SHA256.HashData(content.Data);
        var count = (content.Data.Length + chunkSize - 1) / chunkSize;
        var frames = new List<byte[]>(count);

        for (var index = 0; index < count; index++)
        {
            var offset = index * chunkSize;
            var length = Math.Min(chunkSize, content.Data.Length - offset);
            var frame = new byte[4 + 1 + 16 + 1 + 4 + 4 + 4 + 32 + 4 + length];
            var span = frame.AsSpan();
            Magic.CopyTo(span);
            span[4] = 1;
            transfer.TryWriteBytes(span.Slice(5, 16));
            span[21] = (byte)content.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(22, 4), index);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(26, 4), count);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(30, 4), content.Data.Length);
            hash.CopyTo(span.Slice(34, 32));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(66, 4), length);
            content.Data.AsSpan(offset, length).CopyTo(span.Slice(70));
            frames.Add(frame);
        }

        return frames;
    }

    public static ClipboardChunk Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 70 || !frame[..4].SequenceEqual(Magic) || frame[4] != 1)
            throw new InvalidDataException("Unsupported clipboard frame.");

        var transfer = new Guid(frame.Slice(5, 16));
        var kind = (ClipboardContentKind)frame[21];
        var index = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(22, 4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(26, 4));
        var total = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(30, 4));
        var hash = frame.Slice(34, 32).ToArray();
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(66, 4));

        if (count <= 0 || index < 0 || index >= count || total <= 0 ||
            payloadLength < 0 || frame.Length != 70 + payloadLength)
            throw new InvalidDataException("Invalid clipboard frame metadata.");

        return new ClipboardChunk(transfer, kind, index, count, total, hash, frame.Slice(70).ToArray());
    }
}

public sealed class ClipboardReassembler
{
    private sealed class Pending
    {
        public required ClipboardContentKind Kind;
        public required int Count;
        public required int TotalLength;
        public required byte[] Hash;
        public required DateTimeOffset Created;
        public Dictionary<int, byte[]> Chunks { get; } = [];
    }

    private readonly Dictionary<Guid, Pending> _pending = [];
    private readonly TimeSpan _timeout;

    public ClipboardReassembler(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public ClipboardContent? Add(ClipboardChunk chunk, DateTimeOffset? now = null)
    {
        Cleanup(now ?? DateTimeOffset.UtcNow);

        if (!_pending.TryGetValue(chunk.TransferId, out var pending))
        {
            pending = new Pending
            {
                Kind = chunk.Kind,
                Count = chunk.Count,
                TotalLength = chunk.TotalLength,
                Hash = chunk.Hash.ToArray(),
                Created = now ?? DateTimeOffset.UtcNow
            };
            _pending.Add(chunk.TransferId, pending);
        }
        else if (pending.Kind != chunk.Kind || pending.Count != chunk.Count ||
                 pending.TotalLength != chunk.TotalLength || !pending.Hash.SequenceEqual(chunk.Hash))
        {
            _pending.Remove(chunk.TransferId);
            throw new InvalidDataException("Clipboard transfer metadata changed mid-transfer.");
        }

        pending.Chunks.TryAdd(chunk.Index, chunk.Payload.ToArray());
        if (pending.Chunks.Count != pending.Count) return null;

        using var output = new MemoryStream(pending.TotalLength);
        for (var i = 0; i < pending.Count; i++)
        {
            if (!pending.Chunks.TryGetValue(i, out var part))
                return null;
            output.Write(part);
        }

        _pending.Remove(chunk.TransferId);
        var bytes = output.ToArray();
        if (bytes.Length != pending.TotalLength ||
            !SHA256.HashData(bytes).SequenceEqual(pending.Hash))
            throw new InvalidDataException("Clipboard transfer integrity check failed.");

        return ClipboardContent.Create(pending.Kind, bytes);
    }

    public void Cleanup(DateTimeOffset now)
    {
        foreach (var id in _pending
                     .Where(x => now - x.Value.Created > _timeout)
                     .Select(x => x.Key)
                     .ToArray())
            _pending.Remove(id);
    }
}

public sealed record BleFragment(uint TransferId, ushort Index, ushort Count, byte[] Payload);

public static class BleFragmentCodec
{
    public const int HeaderSize = 10;

    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> message, int maxGattValue, uint transferId)
    {
        if (maxGattValue <= HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(maxGattValue));

        var payloadSize = maxGattValue - HeaderSize;
        var countInt = (message.Length + payloadSize - 1) / payloadSize;
        if (countInt is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(message));

        var count = (ushort)countInt;
        var fragments = new List<byte[]>(count);

        for (ushort i = 0; i < count; i++)
        {
            var offset = i * payloadSize;
            var length = Math.Min(payloadSize, message.Length - offset);
            var frame = new byte[HeaderSize + length];
            frame[0] = 0xC7;
            frame[1] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), transferId);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), i);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8, 2), count);
            message.Slice(offset, length).CopyTo(frame.AsSpan(HeaderSize));
            fragments.Add(frame);
        }

        return fragments;
    }

    public static BleFragment Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderSize || frame[0] != 0xC7 || frame[1] != 1)
            throw new InvalidDataException("Unsupported BLE clipboard fragment.");

        var id = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(2, 4));
        var index = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2));
        if (count == 0 || index >= count)
            throw new InvalidDataException("Invalid BLE fragment metadata.");

        return new BleFragment(id, index, count, frame.Slice(HeaderSize).ToArray());
    }
}

public sealed class BleFragmentReassembler
{
    private readonly Dictionary<uint, (ushort Count, DateTimeOffset Created, Dictionary<ushort, byte[]> Parts)> _pending = [];
    private readonly TimeSpan _timeout;

    public BleFragmentReassembler(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public byte[]? Add(BleFragment fragment, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        foreach (var id in _pending.Where(x => at - x.Value.Created > _timeout).Select(x => x.Key).ToArray())
            _pending.Remove(id);

        if (!_pending.TryGetValue(fragment.TransferId, out var state))
        {
            state = (fragment.Count, at, []);
            _pending[fragment.TransferId] = state;
        }
        else if (state.Count != fragment.Count)
        {
            _pending.Remove(fragment.TransferId);
            throw new InvalidDataException("BLE fragment count changed mid-transfer.");
        }

        state.Parts.TryAdd(fragment.Index, fragment.Payload.ToArray());
        _pending[fragment.TransferId] = state;
        if (state.Parts.Count != state.Count) return null;

        using var output = new MemoryStream();
        for (ushort i = 0; i < state.Count; i++)
        {
            if (!state.Parts.TryGetValue(i, out var bytes)) return null;
            output.Write(bytes);
        }

        _pending.Remove(fragment.TransferId);
        return output.ToArray();
    }
}

public enum ClipboardTransportKind
{
    Lan,
    PeerToPeer,
    BluetoothLe,
    Relay
}

public sealed record ClipboardTransportCapabilities(
    bool Lan,
    bool PeerToPeer,
    bool BluetoothLe,
    bool Relay);

public static class ClipboardTransportPolicy
{
    public const int DefaultBleImageLimit = 512 * 1024;

    public static IReadOnlyList<ClipboardTransportKind> Candidates(
        ClipboardContent content,
        ClipboardTransportCapabilities capabilities,
        bool relayEnabled = false,
        int bleImageLimit = DefaultBleImageLimit)
    {
        var routes = new List<ClipboardTransportKind>(4);
        if (capabilities.Lan) routes.Add(ClipboardTransportKind.Lan);
        if (capabilities.PeerToPeer) routes.Add(ClipboardTransportKind.PeerToPeer);

        var bleAllowed = content.Kind == ClipboardContentKind.TextUtf8 || content.Data.Length <= bleImageLimit;
        if (capabilities.BluetoothLe && bleAllowed) routes.Add(ClipboardTransportKind.BluetoothLe);

        if (relayEnabled && capabilities.Relay) routes.Add(ClipboardTransportKind.Relay);
        return routes;
    }
}


public sealed record ClipboardRelayIdentity(string Room, string Auth);

public static class ClipboardRelayCrypto
{
    private static readonly byte[] RoomLabel = "ConPhone-Relay-Room-v1"u8.ToArray();
    private static readonly byte[] AuthLabel = "ConPhone-Relay-Auth-v1"u8.ToArray();
    private static readonly byte[] AadLabel = "ConPhone-Clipboard-Relay-v1"u8.ToArray();

    public static ClipboardRelayIdentity DeriveIdentity(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> deviceHash)
    {
        if (secret.Length != 32 || deviceHash.Length != 32)
            throw new ArgumentException("Relay secret and device hash must each contain 32 bytes.");

        var roomInput = new byte[RoomLabel.Length + deviceHash.Length];
        RoomLabel.CopyTo(roomInput, 0);
        deviceHash.CopyTo(roomInput.AsSpan(RoomLabel.Length));

        var authInput = new byte[AuthLabel.Length + deviceHash.Length];
        AuthLabel.CopyTo(authInput, 0);
        deviceHash.CopyTo(authInput.AsSpan(AuthLabel.Length));

        var room = HMACSHA256.HashData(secret, roomInput);
        var auth = HMACSHA256.HashData(secret, authInput);
        return new ClipboardRelayIdentity(Base64Url(room), Base64Url(auth));
    }

    public static byte[] EncryptFrame(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> deviceHash,
        ReadOnlySpan<byte> plaintext)
    {
        if (secret.Length != 32 || deviceHash.Length != 32)
            throw new ArgumentException("Relay secret and device hash must each contain 32 bytes.");
        if (plaintext.Length is <= 0 or > 2 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(plaintext));

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var aad = BuildAad(deviceHash);

        using (var aes = new AesGcm(secret, 16))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        var result = new byte[1 + nonce.Length + ciphertext.Length + tag.Length];
        result[0] = 1;
        nonce.CopyTo(result, 1);
        ciphertext.CopyTo(result, 13);
        tag.CopyTo(result, 13 + ciphertext.Length);
        return result;
    }

    public static byte[] DecryptFrame(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> deviceHash,
        ReadOnlySpan<byte> envelope)
    {
        if (secret.Length != 32 || deviceHash.Length != 32)
            throw new ArgumentException("Relay secret and device hash must each contain 32 bytes.");
        if (envelope.Length < 30 || envelope[0] != 1)
            throw new InvalidDataException("Unsupported relay envelope.");

        var nonce = envelope.Slice(1, 12);
        var cipherLength = envelope.Length - 1 - 12 - 16;
        if (cipherLength <= 0 || cipherLength > 2 * 1024 * 1024)
            throw new InvalidDataException("Invalid relay envelope length.");

        var ciphertext = envelope.Slice(13, cipherLength);
        var tag = envelope.Slice(13 + cipherLength, 16);
        var plaintext = new byte[cipherLength];
        var aad = BuildAad(deviceHash);

        try
        {
            using var aes = new AesGcm(secret, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Relay clipboard frame authentication failed.", ex);
        }
    }

    private static byte[] BuildAad(ReadOnlySpan<byte> deviceHash)
    {
        var aad = new byte[AadLabel.Length + deviceHash.Length];
        AadLabel.CopyTo(aad, 0);
        deviceHash.CopyTo(aad.AsSpan(AadLabel.Length));
        return aad;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

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
    public const int HeaderSize = 70;
    public const int MinChunkSize = 256;
    public const int MaxChunkSize = 1024 * 1024;
    public const int MaxEncodedFrameBytes = HeaderSize + MaxChunkSize;
    public const int DefaultChunkSize = 64 * 1024;
    public const int MaxInFlightTransfers = 8;

    public static IReadOnlyList<byte[]> Encode(ClipboardContent content, int chunkSize = DefaultChunkSize)
    {
        if (chunkSize is < MinChunkSize or > MaxChunkSize)
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
            var frame = new byte[HeaderSize + length];
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
        if (frame.Length is < HeaderSize or > MaxEncodedFrameBytes ||
            !frame[..4].SequenceEqual(Magic) || frame[4] != 1)
            throw new InvalidDataException("Unsupported clipboard frame.");

        var transfer = new Guid(frame.Slice(5, 16));
        var kind = (ClipboardContentKind)frame[21];
        var index = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(22, 4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(26, 4));
        var total = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(30, 4));
        var hash = frame.Slice(34, 32).ToArray();
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(66, 4));

        if (!Enum.IsDefined(kind))
            throw new InvalidDataException("Unsupported clipboard content kind.");

        var maxTotal = kind == ClipboardContentKind.TextUtf8
            ? ClipboardContent.MaxTextBytes
            : ClipboardContent.MaxImageBytes;
        var minCount = Math.Max(1, (total + MaxChunkSize - 1) / MaxChunkSize);
        var maxCount = total <= MinChunkSize
            ? 1
            : (total + MinChunkSize - 1) / MinChunkSize;

        if (count < minCount || count > maxCount ||
            index < 0 || index >= count ||
            total <= 0 || total > maxTotal ||
            payloadLength <= 0 || payloadLength > MaxChunkSize ||
            payloadLength > total ||
            frame.Length != HeaderSize + payloadLength)
            throw new InvalidDataException("Invalid clipboard frame metadata.");

        return new ClipboardChunk(transfer, kind, index, count, total, hash, frame.Slice(HeaderSize).ToArray());
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
        public int Bytes;
        public Dictionary<int, byte[]> Chunks { get; } = [];
    }

    private readonly Dictionary<Guid, Pending> _pending = [];
    private readonly Dictionary<Guid, DateTimeOffset> _completed = [];
    private readonly TimeSpan _timeout;

    public ClipboardReassembler(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public ClipboardContent? Add(ClipboardChunk chunk, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        Cleanup(at);

        if (_completed.ContainsKey(chunk.TransferId))
            throw new InvalidDataException("Clipboard transfer replay rejected.");

        if (!_pending.TryGetValue(chunk.TransferId, out var pending))
        {
            if (_pending.Count >= ClipboardWireCodec.MaxInFlightTransfers)
                throw new InvalidDataException("Too many clipboard transfers are in flight.");

            pending = new Pending
            {
                Kind = chunk.Kind,
                Count = chunk.Count,
                TotalLength = chunk.TotalLength,
                Hash = chunk.Hash.ToArray(),
                Created = at
            };
            _pending.Add(chunk.TransferId, pending);
        }
        else if (pending.Kind != chunk.Kind || pending.Count != chunk.Count ||
                 pending.TotalLength != chunk.TotalLength || !pending.Hash.SequenceEqual(chunk.Hash))
        {
            _pending.Remove(chunk.TransferId);
            throw new InvalidDataException("Clipboard transfer metadata changed mid-transfer.");
        }

        if (pending.Chunks.TryGetValue(chunk.Index, out var existingChunk))
        {
            if (!existingChunk.AsSpan().SequenceEqual(chunk.Payload))
            {
                _pending.Remove(chunk.TransferId);
                throw new InvalidDataException("Clipboard duplicate chunk payload changed.");
            }
        }
        else
        {
            if ((long)pending.Bytes + chunk.Payload.Length > pending.TotalLength)
            {
                _pending.Remove(chunk.TransferId);
                throw new InvalidDataException("Clipboard transfer exceeds declared total length.");
            }

            var copy = chunk.Payload.ToArray();
            pending.Chunks.Add(chunk.Index, copy);
            pending.Bytes += copy.Length;
        }

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

        var content = ClipboardContent.Create(pending.Kind, bytes);
        _completed[chunk.TransferId] = at;
        return content;
    }

    public void Cleanup(DateTimeOffset now)
    {
        foreach (var id in _pending
                     .Where(x => now - x.Value.Created > _timeout)
                     .Select(x => x.Key)
                     .ToArray())
            _pending.Remove(id);

        foreach (var id in _completed
                     .Where(x => now - x.Value > _timeout)
                     .Select(x => x.Key)
                     .ToArray())
            _completed.Remove(id);
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
    private sealed class Pending
    {
        public required ushort Count;
        public required DateTimeOffset Created;
        public int Bytes;
        public Dictionary<ushort, byte[]> Parts { get; } = [];
    }

    private readonly Dictionary<uint, Pending> _pending = [];
    private readonly Dictionary<uint, DateTimeOffset> _completed = [];
    private readonly TimeSpan _timeout;

    public BleFragmentReassembler(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public byte[]? Add(BleFragment fragment, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        foreach (var id in _pending.Where(x => at - x.Value.Created > _timeout).Select(x => x.Key).ToArray())
            _pending.Remove(id);
        foreach (var id in _completed.Where(x => at - x.Value > _timeout).Select(x => x.Key).ToArray())
            _completed.Remove(id);

        if (_completed.ContainsKey(fragment.TransferId))
            throw new InvalidDataException("BLE clipboard fragment replay rejected.");

        if (!_pending.TryGetValue(fragment.TransferId, out var state))
        {
            if (_pending.Count >= ClipboardWireCodec.MaxInFlightTransfers)
                throw new InvalidDataException("Too many BLE clipboard frames are in flight.");

            state = new Pending
            {
                Count = fragment.Count,
                Created = at
            };
            _pending[fragment.TransferId] = state;
        }
        else if (state.Count != fragment.Count)
        {
            _pending.Remove(fragment.TransferId);
            throw new InvalidDataException("BLE fragment count changed mid-transfer.");
        }

        if (state.Parts.TryGetValue(fragment.Index, out var existingFragment))
        {
            if (!existingFragment.AsSpan().SequenceEqual(fragment.Payload))
            {
                _pending.Remove(fragment.TransferId);
                throw new InvalidDataException("BLE duplicate fragment payload changed.");
            }
        }
        else
        {
            if ((long)state.Bytes + fragment.Payload.Length >
                ClipboardWireCodec.MaxEncodedFrameBytes)
            {
                _pending.Remove(fragment.TransferId);
                throw new InvalidDataException("BLE clipboard frame exceeds the wire-frame safety limit.");
            }

            var copy = fragment.Payload.ToArray();
            state.Parts.Add(fragment.Index, copy);
            state.Bytes += copy.Length;
        }

        if (state.Parts.Count != state.Count) return null;

        using var output = new MemoryStream(state.Bytes);
        for (ushort i = 0; i < state.Count; i++)
        {
            if (!state.Parts.TryGetValue(i, out var bytes)) return null;
            output.Write(bytes);
        }

        _pending.Remove(fragment.TransferId);
        _completed[fragment.TransferId] = at;
        return output.ToArray();
    }
}

public enum ClipboardTransportKind
{
    PeerToPeer,
    BluetoothLe
}

public sealed record ClipboardTransportCapabilities(
    bool PeerToPeer,
    bool BluetoothLe);

public static class ClipboardTransportPolicy
{
    public const int DefaultBleImageLimit = 512 * 1024;

    public static IReadOnlyList<ClipboardTransportKind> Candidates(
        ClipboardContent content,
        ClipboardTransportCapabilities capabilities,
        int bleImageLimit = DefaultBleImageLimit)
    {
        var routes = new List<ClipboardTransportKind>(2);
        if (capabilities.PeerToPeer) routes.Add(ClipboardTransportKind.PeerToPeer);

        var bleAllowed = content.Kind == ClipboardContentKind.TextUtf8 || content.Data.Length <= bleImageLimit;
        if (capabilities.BluetoothLe && bleAllowed) routes.Add(ClipboardTransportKind.BluetoothLe);

        return routes;
    }
}

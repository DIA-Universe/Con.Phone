using System.Collections.Concurrent;

namespace ConPhone.Core;

// Version 2 retains the BLE fragment layout, but requires an 8-byte receipt
// (C7 02, transport id, fragment index) before sending the next fragment.
public sealed class ReliableBleSender
{
    private readonly ConcurrentDictionary<(string, string), TaskCompletionSource<bool>> _pending = new();

    public bool Accept(string peer, byte[] receipt)
    {
        if (receipt.Length != 8 || receipt[0] != 0xC7 || receipt[1] != 2) return false;
        if (_pending.TryGetValue((peer, Convert.ToHexString(receipt)), out var waiter))
            waiter.TrySetResult(true);
        return true;
    }

    public async Task SendAsync(string peer, byte[] fragment,
        Func<byte[], CancellationToken, Task> transmit, CancellationToken token,
        TimeSpan? receiptTimeout = null)
    {
        var packet = (byte[])fragment.Clone();
        packet[1] = 2;
        var key = (peer, Convert.ToHexString(packet.AsSpan(0, 8)));
        var receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, receipt)) throw new InvalidOperationException("Duplicate reliable BLE send.");
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                token.ThrowIfCancellationRequested();
                await transmit(packet, token).ConfigureAwait(false);
                try
                {
                    await receipt.Task.WaitAsync(receiptTimeout ?? TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException) when (attempt < 3) { }
                catch (TimeoutException error)
                {
                    var index = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6));
                    var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8));
                    throw new TimeoutException($"이미지 조각 {index + 1}/{count} 수신 확인 없음 (4회 시도). 휴대폰 Companion 0.3.6 이상과 연결 상태를 확인하세요.", error);
                }
            }
        }
        finally { _pending.TryRemove(key, out _); }
    }
}

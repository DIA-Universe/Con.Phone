using System.Security.Cryptography;
using System.Text;

namespace ConPhone.Windows;

internal sealed record PeerDevice(string Id, string Name, bool Paired);
internal sealed record PeerInput(bool Keyboard = false, bool Mouse = false,
    bool System = false, bool Consumer = false);

// Device visibility is independent of an active HID subscription. This catalog
// only reads metadata and input snapshots; it owns no GATT session or connection.
internal sealed class PeerCatalog(
    Func<Task<IReadOnlyList<PeerDevice>>> readPaired,
    Func<string, Task<PeerDevice?>> readDevice,
    Func<IReadOnlyList<string>> readTrusted,
    Func<string?> readApproved,
    Func<IReadOnlyDictionary<string, PeerInput>> readInputs,
    Action<string> log)
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, PeerDevice> _metadata = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
    private IReadOnlyList<PeerDevice> _paired = [];
    private string? _lastSummary;

    internal async Task<IReadOnlyList<Peer>> GetAsync()
    {
        await _refresh.WaitAsync().ConfigureAwait(false);
        try
        {
            var trusted = readTrusted();
            var approved = readApproved();
            // Keep incoming devices visible for this process even if their first
            // HID subscription disappears before the user can approve them.
            _observed.UnionWith(readInputs().Keys);
            try { _paired = await readPaired().ConfigureAwait(false); }
            catch (Exception ex) {
                log($"[BLE][Peers] paired metadata unavailable HRESULT=0x{ex.HResult:X8}; keeping previous candidates");
            }
            foreach (var device in _paired) _metadata[device.Id] = device;
            var ids = new HashSet<string>(trusted, StringComparer.Ordinal);
            ids.UnionWith(_paired.Select(device => device.Id));
            ids.UnionWith(_observed);
            if (!string.IsNullOrWhiteSpace(approved)) ids.Add(approved);
            var pairedIds = _paired.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids.Where(id => !pairedIds.Contains(id)))
            {
                try {
                    if (await readDevice(id).ConfigureAwait(false) is { } device)
                        _metadata[id] = device;
                }
                catch (Exception ex) {
                    log($"[BLE][Peers] device metadata unavailable HRESULT=0x{ex.HResult:X8}; retaining row");
                }
            }
            // Read input state after awaits. Cached metadata must never imply a
            // live connection, trust, or permission to send input.
            var inputs = readInputs();
            _observed.UnionWith(inputs.Keys);
            ids.UnionWith(inputs.Keys);
            var peers = ids.Select(id => {
                _metadata.TryGetValue(id, out var device);
                var input = inputs.GetValueOrDefault(id) ?? new PeerInput();
                var isTrusted = trusted.Contains(id, StringComparer.Ordinal);
                return new Peer(id,
                    string.IsNullOrWhiteSpace(device?.Name)
                        ? (isTrusted ? "저장된 기기 (오프라인)" : "이름 확인 불가") : device.Name,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8],
                    input.Keyboard, input.Mouse, input.System, device?.Paired == true,
                    input.Keyboard || input.Mouse, isTrusted, id == approved, input.Consumer);
            }).OrderByDescending(peer => peer.Active).ThenByDescending(peer => peer.Trusted)
                .ThenBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var summary = $"[BLE][Peers] paired={_paired.Count} trusted={trusted.Count} incoming={inputs.Count} rows={peers.Length}";
            if (summary != _lastSummary) { _lastSummary = summary; log(summary); }
            return peers;
        }
        finally { _refresh.Release(); }
    }
}

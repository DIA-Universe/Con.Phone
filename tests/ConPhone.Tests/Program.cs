using ConPhone.Core;

int passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}"); passed++;
}
var keys = new KeyboardState();
Check("A mapping", Hid.Usage(0x41) == 4);
Check("Z mapping", Hid.Usage(0x5A) == 29);
Check("right control mapping", Hid.Usage(0x11, extended: true) == 0xE4);
Check("right shift mapping", Hid.Usage(0x10, 0x36) == 0xE5);
Check("Mac profile left Ctrl to Command", Hid.MapModifierUsage(0xE0, ModifierProfile.MacCtrlCommandSwap) == 0xE3);
Check("Mac profile left Command to Ctrl", Hid.MapModifierUsage(0xE3, ModifierProfile.MacCtrlCommandSwap) == 0xE0);
Check("standard profile keeps Ctrl", Hid.MapModifierUsage(0xE0, ModifierProfile.Standard) == 0xE0);
Check("Korean LANG1", Hid.Usage(0x15) == 0x90);
Check("Hanja LANG2", Hid.Usage(0x19) == 0x91);
Check("keypad enter", Hid.Usage(0x0D, extended: true) == 0x58);
Check("unmapped key", Hid.Usage(0) == 0);
Check("press A", keys.Update(4, true)![2] == 4);
Check("repeat suppressed", keys.Update(4, true) is null);
Check("release A", keys.Update(4, false)!.All(b => b == 0));
keys.Update(0xE0, true); keys.Update(0xE5, true);
Check("modifier bit positions", keys.Snapshot()[0] == 0x21);
keys.Clear();
for (byte i = 4; i < 11; i++) keys.Update(i, true);
Check("7 key rollover explicit", keys.Snapshot().Skip(2).All(b => b == 1));
keys.Update(10, false);
Check("rollover recovery", keys.Snapshot().Skip(2).SequenceEqual(new byte[] { 4, 5, 6, 7, 8, 9 }));
keys.Clear(); Check("clear state", keys.Snapshot().All(b => b == 0));
var ctrlSpace = keys.SnapshotWith(0xE0, 0x2C);
Check("Ctrl+Space synthetic modifier", ctrlSpace[0] == 0x01);
Check("Ctrl+Space synthetic space", ctrlSpace[2] == 0x2C);
var shiftSpace = keys.SnapshotWith(0xE1, 0x2C);
Check("Shift+Space synthetic modifier", shiftSpace[0] == 0x02);
Check("Shift+Space synthetic space", shiftSpace[2] == 0x2C);
var chunks = Hid.Encode(new PointerPacket(1, 300, -300, 2, -1)).ToArray();
Check("pointer report length", chunks.All(r => r.Data.Length == Hid.MouseReportLength));
Check("pointer chunk count", chunks.Length == 1);
static short I16(byte lo, byte hi) => unchecked((short)(lo | (hi << 8)));
Check("pointer x conserved", chunks.Sum(r => (int)I16(r.Data[1], r.Data[2])) == 300);
Check("pointer y conserved", chunks.Sum(r => (int)I16(r.Data[3], r.Data[4])) == -300);
Check("pointer wheel conserved", chunks.Sum(r => (int)unchecked((sbyte)r.Data[5])) == 2);
Check("drag buttons preserved", chunks.All(r => r.Data[0] == 1));
Check("release emitted without motion", Hid.Encode(new PointerPacket(0)).Single().Data.All(b => b == 0));
Check("keyboard no embedded report id", Hid.Encode(new KeyboardPacket(new byte[8])).Single().Data.Length == 8);
Check("system lock report down", Hid.SystemReport(true).SequenceEqual(new byte[] { 0x02 }));
Check("system lock report up", Hid.SystemReport(false).SequenceEqual(new byte[] { 0x00 }));
Check("consumer mute down", Hid.ConsumerReport(ConsumerAction.Mute, true).SequenceEqual(new byte[] { 0x01 }));
Check("consumer volume up down", Hid.ConsumerReport(ConsumerAction.VolumeUp, true).SequenceEqual(new byte[] { 0x02 }));
Check("consumer play pause down", Hid.ConsumerReport(ConsumerAction.PlayPause, true).SequenceEqual(new byte[] { 0x08 }));
Check("consumer release", Hid.ConsumerReport(ConsumerAction.NextTrack, false).SequenceEqual(new byte[] { 0x00 }));
var scaler = new PointerScaler();
scaler.Configure(50, 100, false);
Check("50 percent pointer keeps fractional first step", scaler.Apply(new PointerPacket(0, 1)).X == 0);
Check("50 percent pointer carries fraction", scaler.Apply(new PointerPacket(0, 1)).X == 1);
scaler.Configure(200, 100, false);
Check("200 percent pointer doubles motion", scaler.Apply(new PointerPacket(0, 3, -2)).X == 6);
scaler.Configure(100, 50, false);
Check("50 percent scroll carries first notch", scaler.Apply(new PointerPacket(0, Wheel: 1)).Wheel == 0);
Check("50 percent scroll carries second notch", scaler.Apply(new PointerPacket(0, Wheel: 1)).Wheel == 1);
scaler.Configure(100, 100, true);
Check("scroll inversion", scaler.Apply(new PointerPacket(0, Wheel: 1)).Wheel == -1);
var box = new Outbox(2);
Check("enqueue movement", box.TryAdd(new(1, new PointerPacket(0, 10, 20))));
Check("merge movement", box.TryAdd(new(1, new PointerPacket(0, -5, 10))) && box.Count == 1);
Check("merged displacement", (await box.TakeAsync(default)).Packet == new PointerPacket(0, 5, 30));
box.TryAdd(new(1, new PointerPacket(1, 2)));
box.TryAdd(new(1, new PointerPacket(0, 3)));
Check("button transition is barrier", box.Count == 2);
Check("overflow rejects, never drops keyup", !box.TryAdd(new(1, new KeyboardPacket(new byte[8]))));
Check("button down before up", ((PointerPacket)(await box.TakeAsync(default)).Packet).Buttons == 1);
Check("button up follows down", ((PointerPacket)(await box.TakeAsync(default)).Packet).Buttons == 0);
box.TryAdd(new(1, new PointerPacket(0, 1))); box.TryAdd(new(2, new PointerPacket(0, 1)));
Check("epoch prevents merge", box.Count == 2);
box.Clear(); Check("clear removes backlog", box.Count == 0);
using (var cts = new CancellationTokenSource(40))
{
    bool cancelled = false;
    try { await box.TakeAsync(cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check("empty queue cancellation", cancelled);
}
box.TryAdd(new(1, new PointerPacket(0, 32767)));
Check("oversized coalescing fails safe", !box.TryAdd(new(1, new PointerPacket(0, 1))));
// Parse HID short items independently and calculate input/output bit counts per report ID.
var map = Hid.ReportMap;
int reportId = 0, reportSize = 0, reportCount = 0, depth = 0;
var bits = new Dictionary<(int, int), int>();
for (int i = 0; i < map.Length;)
{
    byte prefix = map[i++];
    int length = (prefix & 3) == 3 ? 4 : prefix & 3;
    if (i + length > map.Length) throw new Exception("Truncated HID descriptor");
    int value = 0; for (int j = 0; j < length; j++) value |= map[i++] << (j * 8);
    int item = prefix & 0xFC;
    if (item == 0x84) reportId = value;
    if (item == 0x74) reportSize = value;
    if (item == 0x94) reportCount = value;
    if (item is 0x80 or 0x90) bits[(reportId, item)] = bits.GetValueOrDefault((reportId, item)) + reportSize * reportCount;
    if (item == 0xA0) depth++;
    if (item == 0xC0) depth--;
    if (depth < 0) throw new Exception("Invalid collection nesting");
}
Check("descriptor collections balanced", depth == 0);
Check("descriptor keyboard input 64 bits", bits[(1, 0x80)] == 64);
Check("descriptor has no output reports", !bits.ContainsKey((1, 0x90)));
Check("descriptor pointer input 48 bits", bits[(2, 0x80)] == 48);
Check("descriptor system-control input 8 bits", bits[(3, 0x80)] == 8);
Check("descriptor consumer-control input 8 bits", bits[(4, 0x80)] == 8);

Check("pointer pacing fallback 16ms", PointerPacing.FromBleConnectionInterval(0) == 16);
Check("pointer pacing 12 units = 15ms", PointerPacing.FromBleConnectionInterval(12) == 15);
Check("pointer pacing 24 units = 30ms", PointerPacing.FromBleConnectionInterval(24) == 30);
Check("pointer pacing lower clamp", PointerPacing.FromBleConnectionInterval(6) == 8);

var p2pSecret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
var p2pDeviceHash = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
var p2pClientNonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
var p2pServerNonce = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
var p2pSessionKey = P2PSecurity.DeriveSessionKey(
    p2pSecret,
    p2pDeviceHash,
    p2pClientNonce,
    p2pServerNonce);
Check(
    "P2P v2 session KDF vector",
    Convert.ToHexString(p2pSessionKey) ==
    "16BE739CCB5056785C93854CFD3C384105BE7F799292A5BEBA8FFE2BE73248D0");

// Clipboard text/image sync protocol.
var clipText = ClipboardContent.FromText(string.Concat(Enumerable.Repeat("한글🙂abc", 120)));
Check("clipboard text utf8 roundtrip", clipText.GetText() == string.Concat(Enumerable.Repeat("한글🙂abc", 120)));
Check("clipboard text hash sha256", clipText.Hash.Length == 64);
var clipFrames = ClipboardWireCodec.Encode(clipText, 256);
Check("clipboard chunked into multiple frames", clipFrames.Count > 1);
var clipReassembler = new ClipboardReassembler();
ClipboardContent? rebuilt = null;
foreach (var frame in clipFrames.Reverse())
    rebuilt = clipReassembler.Add(ClipboardWireCodec.Decode(frame)) ?? rebuilt;
Check("clipboard out-of-order reassembly", rebuilt is not null && rebuilt.GetText() == clipText.GetText());
var duplicateReassembler = new ClipboardReassembler();
var firstChunk = ClipboardWireCodec.Decode(clipFrames[0]);
Check("clipboard duplicate first chunk accepted", duplicateReassembler.Add(firstChunk) is null);
Check("clipboard duplicate does not complete early", duplicateReassembler.Add(firstChunk) is null);
ClipboardContent? duplicateResult = null;
foreach (var frame in clipFrames.Skip(1))
    duplicateResult = duplicateReassembler.Add(ClipboardWireCodec.Decode(frame)) ?? duplicateResult;
Check("clipboard duplicate chunk reassembly", duplicateResult?.Hash == clipText.Hash);
bool clipboardReplayRejected = false;
try
{
    duplicateReassembler.Add(ClipboardWireCodec.Decode(clipFrames[0]));
}
catch (InvalidDataException)
{
    clipboardReplayRejected = true;
}
Check("clipboard completed transfer replay rejected", clipboardReplayRejected);

var conflictReassembler = new ClipboardReassembler();
var conflictChunk = ClipboardWireCodec.Decode(clipFrames[0]);
_ = conflictReassembler.Add(conflictChunk);
var changedPayload = conflictChunk.Payload.ToArray();
changedPayload[0] ^= 0x01;
bool conflictingDuplicateRejected = false;
try
{
    conflictReassembler.Add(conflictChunk with { Payload = changedPayload });
}
catch (InvalidDataException)
{
    conflictingDuplicateRejected = true;
}
Check("clipboard conflicting duplicate rejected", conflictingDuplicateRejected);

var oversizedMetadata = clipFrames[0].ToArray();
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
    oversizedMetadata.AsSpan(30, 4),
    ClipboardContent.MaxImageBytes + 1);
bool oversizedTotalRejected = false;
try { _ = ClipboardWireCodec.Decode(oversizedMetadata); }
catch (InvalidDataException) { oversizedTotalRejected = true; }
Check("clipboard oversized declared total rejected", oversizedTotalRejected);

var pngBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
var clipImage = ClipboardContent.FromImage(ClipboardContentKind.Png, pngBytes);
Check("clipboard png signature accepted", clipImage.Kind == ClipboardContentKind.Png);
bool badPngRejected = false;
try { ClipboardContent.FromImage(ClipboardContentKind.Png, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); }
catch (ArgumentException) { badPngRejected = true; }
Check("clipboard bad png rejected", badPngRejected);
bool oversizedWireFrameRejected = false;
try
{
    ClipboardWireCodec.Decode(new byte[ClipboardWireCodec.MaxEncodedFrameBytes + 1]);
}
catch (InvalidDataException)
{
    oversizedWireFrameRejected = true;
}
Check("oversized clipboard wire frame rejected", oversizedWireFrameRejected);
bool zeroPayloadFrameRejected = false;
try
{
    var validFrame = ClipboardWireCodec.Encode(clipText, 4096).Single().ToArray();
    // payloadLength at byte 66; force 0 and trim to header-only.
    Array.Clear(validFrame, 66, 4);
    ClipboardWireCodec.Decode(validFrame[..ClipboardWireCodec.HeaderSize]);
}
catch (InvalidDataException)
{
    zeroPayloadFrameRejected = true;
}
Check("zero payload clipboard frame rejected", zeroPayloadFrameRejected);

bool oversizedDeclaredTotalRejected = false;
try
{
    var invalidTotal = ClipboardWireCodec.Encode(clipText, 4096).Single().ToArray();
    var tooLarge = ClipboardContent.MaxTextBytes + 1;
    invalidTotal[30] = (byte)(tooLarge & 0xFF);
    invalidTotal[31] = (byte)((tooLarge >> 8) & 0xFF);
    invalidTotal[32] = (byte)((tooLarge >> 16) & 0xFF);
    invalidTotal[33] = (byte)((tooLarge >> 24) & 0xFF);
    ClipboardWireCodec.Decode(invalidTotal);
}
catch (InvalidDataException)
{
    oversizedDeclaredTotalRejected = true;
}
Check("oversized declared clipboard total rejected", oversizedDeclaredTotalRejected);

var wholeFrame = ClipboardWireCodec.Encode(clipText, 4096).Single();
var bleParts = BleFragmentCodec.Split(wholeFrame, 20, 42);
Check("ble clipboard uses mtu-sized fragments", bleParts.All(x => x.Length <= 20) && bleParts.Count > 1);
var bleReassembler = new BleFragmentReassembler();
byte[]? bleMessage = null;
foreach (var part in bleParts.Reverse())
    bleMessage = bleReassembler.Add(BleFragmentCodec.Decode(part)) ?? bleMessage;
Check("ble fragment out-of-order reassembly", bleMessage is not null && bleMessage.SequenceEqual(wholeFrame));
bool bleReplayRejected = false;
try
{
    bleReassembler.Add(BleFragmentCodec.Decode(bleParts[0]));
}
catch (InvalidDataException)
{
    bleReplayRejected = true;
}
Check("ble completed fragment replay rejected", bleReplayRejected);

var bleConflict = new BleFragmentReassembler();
var firstBle = BleFragmentCodec.Decode(bleParts[0]);
_ = bleConflict.Add(firstBle);
var changedBlePayload = firstBle.Payload.ToArray();
changedBlePayload[0] ^= 0x01;
bool bleConflictRejected = false;
try { _ = bleConflict.Add(firstBle with { Payload = changedBlePayload }); }
catch (InvalidDataException) { bleConflictRejected = true; }
Check("ble conflicting duplicate fragment rejected", bleConflictRejected);

var allRoutes = ClipboardTransportPolicy.Candidates(
    clipText,
    new ClipboardTransportCapabilities(PeerToPeer: true, BluetoothLe: true));
Check("transport priority p2p first", allRoutes.SequenceEqual(new[]
{
    ClipboardTransportKind.PeerToPeer,
    ClipboardTransportKind.BluetoothLe
}));
var largePng = new byte[ClipboardTransportPolicy.DefaultBleImageLimit + 1];
pngBytes.CopyTo(largePng, 0);
var largeImage = ClipboardContent.FromImage(ClipboardContentKind.Png, largePng);
var largeRoutes = ClipboardTransportPolicy.Candidates(
    largeImage,
    new ClipboardTransportCapabilities(PeerToPeer: false, BluetoothLe: true));
Check("large image skips ble when no fast local path", largeRoutes.Count == 0);


Console.WriteLine($"{passed} checks passed. No Windows/Bluetooth/phone hardware has been tested by these checks.");

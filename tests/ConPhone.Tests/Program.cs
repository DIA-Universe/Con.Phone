using ConPhone.Core;

int passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}"); passed++;
}
var keys = new KeyboardState();
Check("HTML-only Teams fragment preserves paragraphs and Unicode", HtmlClipboardText.Extract("<html>outside<!--StartFragment--><p>한글 &amp; 🙂</p><p>둘<br>셋</p><!--EndFragment-->outside</html>") == "한글 & 🙂\n둘\n셋");
Check("HTML text excludes scripts, styles, attributes and images", HtmlClipboardText.Extract("<style>secret</style><script>secret</script><a href='https://example.com/private'>복사</a><img src='https://example.com/image'>") == "복사");
Check("HTML table keeps cell boundaries", HtmlClipboardText.Extract("<table><tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></table>") == "A\tB\nC\tD");
Check("HTML preformatted text retains indentation", HtmlClipboardText.Extract("<pre>one\n  two</pre>") == "one\n  two");
var htmlPrefix = "Version:1.0\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n<html>제외🙂";
var selectedHtml = "<b>선택🙂</b>";
var htmlStart = System.Text.Encoding.UTF8.GetByteCount(htmlPrefix);
var htmlEnd = htmlStart + System.Text.Encoding.UTF8.GetByteCount(selectedHtml);
var cfHtml = htmlPrefix.Replace("StartFragment:0000000000", $"StartFragment:{htmlStart:D10}").Replace("EndFragment:0000000000", $"EndFragment:{htmlEnd:D10}") + selectedHtml + "제외</html>";
Check("CF_HTML uses byte offsets for Korean and emoji", HtmlClipboardText.Extract(cfHtml) == "선택🙂");
try { HtmlClipboardText.Extract("Version:1.0\r\nStartFragment:99999\r\nEndFragment:999999\r\n<html>unselected</html>"); Check("malformed CF_HTML is rejected", false); }
catch (InvalidDataException) { Check("malformed CF_HTML is rejected", true); }
Check("Galaxy lock keeps GUI+L", Hid.LockKeyboardReport(ModifierProfile.Standard).SequenceEqual(new byte[] { 8,0,15,0,0,0,0,0 }));
Check("Apple lock sends Control+Command+Q without remapping", Hid.LockKeyboardReport(ModifierProfile.MacCtrlCommandSwap).SequenceEqual(new byte[] { 9,0,20,0,0,0,0,0 }));
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
Check("pointer horizontal pan conserved", chunks.Sum(r => (int)unchecked((sbyte)r.Data[6])) == -1);
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
Check(
    "extreme pointer delta starts a second queue item instead of failing",
    box.TryAdd(new(1, new PointerPacket(0, 1))) &&
    box.Count == 2);
Check(
    "extreme pointer first segment preserved",
    (await box.TakeAsync(default)).Packet == new PointerPacket(0, 32767));
Check(
    "extreme pointer overflow segment preserved",
    (await box.TakeAsync(default)).Packet == new PointerPacket(0, 1));
// Parse HID short items independently and calculate input/output bit counts per report ID.
var moving = new Envelope(7, new PointerPacket(0, 2, 3));
box.TryAdd(new(7, new PointerPacket(0, 8, -3)));
box.TryAdd(new(7, new KeyboardPacket(new byte[8])));
Check("pacing includes newly arrived motion", box.CoalescePendingMotion(moving).Packet == new PointerPacket(0, 10, 0));
Check("pacing preserves keyboard barrier", (await box.TakeAsync(default)).Packet is KeyboardPacket);
box.TryAdd(new(7, new PointerPacket(1, 8)));
Check("pacing preserves click barrier", box.CoalescePendingMotion(moving) == moving && box.Count == 1);
box.Clear();
box.TryAdd(new(8, new PointerPacket(0, 8)));
Check("pacing preserves target boundary", box.CoalescePendingMotion(moving) == moving && box.Count == 1);
box.Clear();
box.TryAdd(new(7, new PointerPacket(0, 8, Wheel: 1)));
Check("pacing preserves scroll barrier", box.CoalescePendingMotion(moving) == moving && box.Count == 1);
box.Clear();
box.TryAdd(new(7, new PointerPacket(0, -32767)));
box.TryAdd(new(7, new PointerPacket(0, -1)));
Check("negative delta cannot merge outside HID range", box.Count == 2);
Check("negative extreme still encodes", Hid.Encode((await box.TakeAsync(default)).Packet).Count() == 1);
box.Clear();
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
Check("descriptor pointer input 56 bits", bits[(2, 0x80)] == 56);
Check("descriptor system-control input 8 bits", bits[(3, 0x80)] == 8);
Check("descriptor consumer-control input 8 bits", bits[(4, 0x80)] == 8);

Check("pointer pacing fallback 16ms", PointerPacing.FromBleConnectionInterval(0) == 16);
Check("pointer pacing 12 units = 15ms", PointerPacing.FromBleConnectionInterval(12) == 15);
Check("pointer pacing 24 units = 30ms", PointerPacing.FromBleConnectionInterval(24) == 30);
Check("pointer pacing lower clamp", PointerPacing.FromBleConnectionInterval(6) == 8);
Check("pointer pacing preserves 100ms negotiated interval", PointerPacing.FromBleConnectionInterval(80) == 100);
Check("pointer pacing preserves 250ms negotiated interval", PointerPacing.FromBleConnectionInterval(200) == 250);

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

var ackId = Guid.NewGuid();
var ackFrame = ClipboardAckCodec.Encode(ackId);
Check("clipboard ACK fixed length", ackFrame.Length == ClipboardAckCodec.FrameLength);
Check("clipboard ACK roundtrip",
    ClipboardAckCodec.TryDecode(ackFrame, out var ackDecoded) &&
    ackDecoded == ackId);
Check("clipboard ACK rejects data frame",
    !ClipboardAckCodec.TryDecode(new byte[] { 1, 2, 3 }, out _));

var ackBleFragments = BleFragmentCodec.Split(
    ackFrame,
    maxGattValue: 20,
    transferId: 0xAABBCCDD);
Check(
    "clipboard ACK fragments fit MTU-23 payload",
    ackBleFragments.Count > 1 &&
    ackBleFragments.All(x => x.Length <= 20));

var ackBleReassembler = new BleFragmentReassembler();
byte[]? rebuiltAck = null;
foreach (var fragment in ackBleFragments.Reverse())
{
    rebuiltAck =
        ackBleReassembler.Add(
            BleFragmentCodec.Decode(fragment))
        ?? rebuiltAck;
}

Check(
    "clipboard ACK survives BLE fragmentation",
    rebuiltAck is not null &&
    ClipboardAckCodec.TryDecode(
        rebuiltAck,
        out var rebuiltAckId) &&
    rebuiltAckId == ackId);

var targetOrder = new[] { "A", "B", "C", "D" };
var allReadyTargets = new HashSet<string>(targetOrder, StringComparer.Ordinal);
Check("target cycle A -> B", TargetCycle.Next(targetOrder, allReadyTargets, "A") == "B");
Check("target cycle B -> C", TargetCycle.Next(targetOrder, allReadyTargets, "B") == "C");
Check("target cycle C -> D", TargetCycle.Next(targetOrder, allReadyTargets, "C") == "D");
Check("target cycle D -> A", TargetCycle.Next(targetOrder, allReadyTargets, "D") == "A");
var partialReadyTargets = new HashSet<string>(new[] { "A", "C" }, StringComparer.Ordinal);
Check("target cycle skips offline B", TargetCycle.Next(targetOrder, partialReadyTargets, "A") == "C");
Check("target cycle wraps partial set", TargetCycle.Next(targetOrder, partialReadyTargets, "C") == "A");
Check("target cycle chooses first ready when current offline", TargetCycle.Next(targetOrder, partialReadyTargets, "B") == "A");
Check("target cycle none when no target ready", TargetCycle.Next(targetOrder, new HashSet<string>(), "A") is null);

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


// Fault injection on the actual reliable sender: lose data, then lose its receipt.
var reliable = new ReliableBleSender();
var reliablePacket = BleFragmentCodec.Split(pngBytes, 182, 123).First();
var transmissions = 0;
await reliable.SendAsync("phone", reliablePacket, (packet, ct) =>
{
    transmissions++;
    Check("reliable packet uses v2", packet[1] == 2);
    if (transmissions == 2)
        reliable.Accept("other-phone", packet.Take(8).ToArray());
    if (transmissions == 3)
        reliable.Accept("phone", packet.Take(8).ToArray());
    return Task.CompletedTask;
}, CancellationToken.None, TimeSpan.FromMilliseconds(10));
Check("lost notification and receipt recover; wrong peer ignored", transmissions == 3);
var exhausted = 0;
try
{
    await reliable.SendAsync("phone", reliablePacket, (_, _) =>
    {
        exhausted++;
        return Task.CompletedTask;
    }, CancellationToken.None, TimeSpan.FromMilliseconds(5));
    Check("missing receipts must fail", false);
}
catch (TimeoutException) { Check("retry budget bounded", exhausted == 4); }
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        await reliable.SendAsync("phone", reliablePacket, (_, _) => throw new Exception("must not transmit"), cancelled.Token);
        Check("cancel must stop", false);
    }
    catch (OperationCanceledException) { Check("cancel stops transmission", true); }
}
Check("unrelated ACK left for application decoder", !reliable.Accept("phone", new byte[22]));

// Continuous pointer activity must leave gaps even on retries, but cannot
// starve text indefinitely. Fake time measures scheduling, not radio throughput.
var busyYields = 0;
Task RecordYield(TimeSpan duration, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    if (duration != TimeSpan.FromMilliseconds(48)) throw new Exception("Unexpected input gap");
    busyYields++;
    return Task.CompletedTask;
}
await InputAwareClipboardPacing.YieldAsync(() => false, CancellationToken.None, RecordYield);
Check("idle clipboard has no input delay", busyYields == 0);
var pacedAttempts = 0;
await reliable.SendAsync("phone", reliablePacket, async (packet, ct) =>
{
    await InputAwareClipboardPacing.YieldAsync(() => true, ct, RecordYield);
    if (++pacedAttempts == 3) reliable.Accept("phone", packet.Take(8).ToArray());
}, CancellationToken.None, TimeSpan.FromMilliseconds(5));
Check("busy input yields on initial notification and every retry", busyYields == 3);
Check("continuous input does not starve clipboard delivery", pacedAttempts == 3);
using (var pacingCancel = new CancellationTokenSource())
{
    var waiting = InputAwareClipboardPacing.YieldAsync(() => true, pacingCancel.Token,
        (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
    pacingCancel.Cancel();
    try { await waiting; Check("input pacing cancellation", false); }
    catch (OperationCanceledException) { Check("input pacing cancellation", true); }
}

var preciseScaler = new PointerScaler();
preciseScaler.Configure(1, 100, false);
var exactOnePercent = Enumerable.Range(0, 100).Select(_ => preciseScaler.Apply(new PointerPacket(0, 1, -1))).ToArray();
Check("1 percent accumulates exactly one positive pixel", exactOnePercent.Sum(x => x.X) == 1);
Check("1 percent accumulates exactly one negative pixel", exactOnePercent.Sum(x => x.Y) == -1);
preciseScaler.Configure(101, 100, false);
Check("101 percent accumulates exactly 101 pixels", Enumerable.Range(0, 100).Sum(_ => preciseScaler.Apply(new PointerPacket(0, 1)).X) == 101);

// Recovery waits for subscriptions without replaying an already submitted HID
// report. Replay would double relative mouse movement/scrolls and held keys.
var readinessChecks = 0;
var submittedReports = new List<string>();
var recoveryCurrent = true;
foreach (var reportName in new[] { "key down", "key up", "mouse delta", "button up" })
{
    var sentReport = await HidSendRecovery.SendAsync(() => recoveryCurrent, _ =>
    {
        if (++readinessChecks <= 2) return Task.FromResult(false);
        submittedReports.Add(reportName);
        return Task.FromResult(true);
    }, CancellationToken.None);
    Check($"recovered HID {reportName} is sent", sentReport);
}
Check("HID recovery preserves report order exactly once", submittedReports.SequenceEqual(
    new[] { "key down", "key up", "mouse delta", "button up" }));
var delayedNotifications = 0;
Check("750ms notification delay does not force local input", await HidSendRecovery.SendAsync(
    () => true, async token => { delayedNotifications++; await Task.Delay(850, token); return true; },
    CancellationToken.None) && delayedNotifications == 1);
var ambiguousAttempts = 0;
try
{
    await HidSendRecovery.SendAsync(() => true, _ =>
    {
        ambiguousAttempts++;
        throw new IOException("notification delivery unknown");
    }, CancellationToken.None);
    Check("ambiguous notification must fail", false);
}
catch (IOException) { Check("ambiguous mouse notification is never replayed", ambiguousAttempts == 1); }
try
{
    await HidSendRecovery.SendAsync(() => true, _ => Task.FromResult(false),
        CancellationToken.None, TimeSpan.FromMilliseconds(100));
    Check("permanent disconnect must time out", false);
}
catch (TimeoutException) { Check("permanent disconnect has bounded recovery", true); }
var pendingOldSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var oldSend = HidSendRecovery.SendAsync(() => recoveryCurrent, _ => pendingOldSend.Task, CancellationToken.None);
recoveryCurrent = false;
pendingOldSend.SetException(new IOException("old session closed"));
Check("late failure from previous activation is ignored", !await oldSend);
var supersededAttempts = 0;
Check("superseded input cannot reach a new target", !await HidSendRecovery.SendAsync(() => false,
    _ => { supersededAttempts++; return Task.FromResult(true); }, CancellationToken.None) && supersededAttempts == 0);
using (var recoveryCancellation = new CancellationTokenSource())
{
    var pending = HidSendRecovery.SendAsync(() => true,
        async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; },
        recoveryCancellation.Token);
    recoveryCancellation.Cancel();
    try { await pending; Check("shutdown must cancel recovery", false); }
    catch (OperationCanceledException) { Check("shutdown cancels HID recovery", true); }
}

var cleanupOrder = new List<string>();
var resourceLifetime = new BleResourceLifetime();
resourceLifetime.Token.Register(() => cleanupOrder.Add("cancel"));
resourceLifetime.Register(() => cleanupOrder.Add("provider stop"));
resourceLifetime.Register(() => throw new IOException("already disconnected"));
resourceLifetime.Register(() => cleanupOrder.Add("detach callback"));
resourceLifetime.Dispose(); resourceLifetime.Dispose();
resourceLifetime.Register(() => cleanupOrder.Add("late resource released"));
Check("BLE cleanup cancels first, survives errors, is idempotent and releases late resources",
    cleanupOrder.SequenceEqual(new[] { "cancel", "detach callback", "provider stop", "late resource released" }));
var concurrentCleanup = 0;
var racedLifetime = new BleResourceLifetime();
await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => {
    racedLifetime.Register(() => Interlocked.Increment(ref concurrentCleanup));
    if (i % 3 == 0) racedLifetime.Dispose();
})));
racedLifetime.Dispose();
Check("concurrent BLE registration and cleanup releases all 100 resources exactly once", concurrentCleanup == 100);
var cleanupDespiteLoggingError = 0;
var badLoggerLifetime = new BleResourceLifetime(_ => throw new IOException("logger unavailable"));
badLoggerLifetime.Register(() => cleanupDespiteLoggingError++);
badLoggerLifetime.Dispose();
Check("diagnostic logging failure cannot interrupt BLE cleanup", cleanupDespiteLoggingError == 1);
var sharedSession = new TestBleResource();
var hidLease = SharedBleResource<TestBleResource>.Acquire(sharedSession);
var clipboardLease = SharedBleResource<TestBleResource>.Acquire(sharedSession);
clipboardLease.Dispose(); clipboardLease.Dispose();
Check("clipboard unsubscribe does not dispose a session still owned by HID", sharedSession.Disposals == 0);
hidLease.Dispose(); hidLease.Dispose();
Check("last shared GATT owner disposes exactly once", sharedSession.Disposals == 1);
try { SharedBleResource<TestBleResource>.Acquire(sharedSession); Check("closed GATT cannot be reused", false); }
catch (ObjectDisposedException) { Check("closed GATT cannot be reused", true); }

var resetContent = ClipboardContent.FromText("재연결 UTF-8🙂 " + new string('x', 1000));
var resetFrames = ClipboardWireCodec.Enumerate(resetContent, 256).Select(x => ClipboardWireCodec.Decode(x)).ToArray();
var resetWire = new ClipboardReassembler();
resetWire.Add(resetFrames[0]); resetWire.ClearPending();
ClipboardContent? afterReset = null;
foreach (var frame in resetFrames.Skip(1)) afterReset = resetWire.Add(frame);
Check("cleanup drops incomplete clipboard chunks", afterReset is null);
afterReset = resetWire.Add(resetFrames[0]);
Check("new session can reassemble UTF8 out of order after cleanup", afterReset?.GetText() == resetContent.GetText());
resetWire.ClearPending();
try { resetWire.Add(resetFrames[0]); Check("cleanup preserves clipboard replay protection", false); }
catch (InvalidDataException) { Check("cleanup preserves clipboard replay protection", true); }
var resetPackets = BleFragmentCodec.Split(new byte[100], 20, 419u).Select(x => BleFragmentCodec.Decode(x)).ToArray();
var resetBle = new BleFragmentReassembler();
resetBle.Add(resetPackets[0]); resetBle.ClearPending();
byte[]? resetFrame = null;
foreach (var packet in resetPackets.Skip(1)) resetFrame = resetBle.Add(packet);
Check("cleanup drops incomplete MTU fragments", resetFrame is null);
Check("MTU assembly resumes without old partial bytes", resetBle.Add(resetPackets[0])?.Length == 100);
resetBle.ClearPending();
try { resetBle.Add(resetPackets[0]); Check("cleanup preserves BLE replay protection", false); }
catch (InvalidDataException) { Check("cleanup preserves BLE replay protection", true); }

var cancellationSender = new ReliableBleSender();
var cancellableFragment = BleFragmentCodec.Split(new byte[10], 20, 991u).First();
using (var sendCancellation = new CancellationTokenSource())
{
    var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var interruptedSend = cancellationSender.SendAsync("test-peer", cancellableFragment,
        (_, _) => { submitted.TrySetResult(); return Task.CompletedTask; }, sendCancellation.Token);
    await submitted.Task;
    sendCancellation.Cancel();
    try { await interruptedSend; Check("interrupted chunk must cancel", false); }
    catch (OperationCanceledException) { Check("disconnect cancels an outstanding fragment receipt wait", true); }
}
var freshReceiptAccepted = false;
await cancellationSender.SendAsync("test-peer", cancellableFragment, (packet, _) => {
    freshReceiptAccepted = cancellationSender.Accept("test-peer", packet[..8]);
    return Task.CompletedTask;
}, CancellationToken.None);
Check("cancelled receipt waiter does not occupy the next session transfer", freshReceiptAccepted);

Console.WriteLine($"{passed} checks passed. No Windows/Bluetooth/phone hardware has been tested by these checks.");

sealed class TestBleResource : IDisposable
{
    public int Disposals;
    public void Dispose() => Interlocked.Increment(ref Disposals);
}

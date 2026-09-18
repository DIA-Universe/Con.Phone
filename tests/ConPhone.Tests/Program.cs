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
Console.WriteLine($"{passed} checks passed. No Windows/Bluetooth/phone hardware has been tested by these checks.");

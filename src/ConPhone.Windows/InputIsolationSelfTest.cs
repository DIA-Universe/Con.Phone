using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ConPhone.Core;

namespace ConPhone.Windows;

internal static class InputIsolationSelfTest
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }
    private static Task<nint> Key(InputCapture input, int key, bool up = false) => input.OnInputThreadAsync(() =>
    {
        var data = Marshal.AllocHGlobal(24);
        try
        {
            for (var i = 0; i < 24; i += 4) Marshal.WriteInt32(data, i, 0);
            Marshal.WriteInt32(data, key);
            Marshal.WriteInt32(data, 8, up ? 0x80 : 0);
            return (nint)typeof(InputCapture).GetMethod("KeyboardHook", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(input, [0, (nint)(up ? 0x101 : 0x100), data])!;
        }
        finally { Marshal.FreeHGlobal(data); }
    });

    internal static async Task RunAsync()
    {
        using var input = new InputCapture(_ => 0);
        await input.Started;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delivered = new List<CapturedInput>();
        input.Packet += captured =>
        {
            lock (delivered) delivered.Add(captured);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3)); // Simulate an unresponsive transport observer.
        };
        try
        {
            input.Activate();
            var old = input.Generation;
            var clock = Stopwatch.StartNew();
            Check(await Key(input, 0x41) == 1, "remote hook suppresses the key before queued dispatch");
            Check(clock.ElapsedMilliseconds < 150, "slow observer cannot block the Windows hook");
            Check(entered.Wait(1000), "packet consumer started independently");
            await Key(input, 0x41, true);
            while (input.IsRemote && clock.ElapsedMilliseconds < 1000) await Task.Delay(10);
            Check(!input.IsRemote && clock.ElapsedMilliseconds < 1000, "500ms dispatch watchdog returns locally without UI or BLE");
            clock.Restart();
            Check(await Key(input, 0x42) != 1, "ordinary PC input passes while old consumer remains blocked");
            await Key(input, 0x42, true);
            Check(clock.ElapsedMilliseconds < 150, "local key path does not wait for transport recovery");
            input.Activate();
            var newer = input.Generation;
            Check(newer != old && !input.DeactivateIfGeneration(old) && input.IsRemote,
                "late failure from an old activation cannot cancel a new one");
            clock.Restart();
            await Key(input, 0x78);
            Check(!input.IsRemote && clock.ElapsedMilliseconds < 150,
                "F9 returns immediately even while the packet observer is blocked");
            await Key(input, 0x78, true);
            release.Set();
            await Task.Delay(100);
            lock (delivered) Check(delivered.Count == 1, "queued old key-up is not delivered after reactivation");
            await input.OnInputThreadAsync(() => true); // Drain owner-thread cleanup.
            input.Activate();
            await Key(input, 0x77);
            await Key(input, 0x77, true);
            Check(!input.IsRemote, "F8 returns locally without calibration or transport work");
            input.Activate();
            await Key(input, 0x78);
            await Key(input, 0x78, true);
            Check(!input.IsRemote, "F9 returns locally immediately");

            // A late activation snapshot may not overwrite a newer safety return.
            using var snapshotStarted = new ManualResetEventSlim();
            using var finishSnapshot = new ManualResetEventSlim();
            var block = false;
            using var competing = new InputCapture(key =>
            {
                if (block && key == 1) { snapshotStarted.Set(); finishSnapshot.Wait(1000); }
                return 0;
            });
            await competing.Started;
            block = true;
            var activationGeneration = competing.Generation;
            var activation = competing.ActivateWhenReleasedAsync(
                () => competing.Generation == activationGeneration, null, CancellationToken.None);
            Check(snapshotStarted.Wait(1000), "activation key snapshot started");
            competing.Deactivate();
            finishSnapshot.Set();
            Check(!await activation && !competing.IsRemote, "safety return cancels pending manual activation without a later retry");
        }
        finally { release.Set(); input.Deactivate(); }
        Console.WriteLine("PASS: Native input-thread isolation and stale-generation tests; physical Bluetooth/phone NOT tested.");
    }
}

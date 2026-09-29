namespace ConPhone.Windows;

public sealed partial class MainForm
{
    internal static void RunPeerPresentationSelfTest()
    {
        using var form = new MainForm(previewMode: true);
        try
        {
            // Create the actual list handle without showing a window or starting
            // the production timers, BLE services, or clipboard capture.
            _ = form._peers.Handle;
            form._peers.Items.Clear();
            var phone = new Peer("phone", "Test phone", "12345678", false, false, false,
                true, false, BluetoothConnected: true);
            form._peers.Items.Add(phone);
            form._peers.SelectedIndex = 0;
            form.ApplyPeerInputStates(new Dictionary<string, PeerInput> { ["phone"] = new(true, true) });
            if (form._peers.SelectedItem is not Peer { Id: "phone", InputReady: true, BluetoothConnected: true })
                throw new InvalidOperationException("Input refresh lost selection or failed to update the actual ListBox row.");
            Console.WriteLine("PASS: WinForms list changes from HID waiting to ready while preserving selected phone.");
            form.ApplyPeerInputStates(new Dictionary<string, PeerInput> { ["phone"] = new(true, true, Suspended: true) });
            if (form._peers.SelectedItem is not Peer { InputReady: false, Suspended: true })
                throw new InvalidOperationException("WinForms list still presents suspended HID as ready.");
            Console.WriteLine("PASS: WinForms list shows input suspend independently of Bluetooth connection.");
            form.ApplyPeerInputStates(new Dictionary<string, PeerInput>());
            if (form._peers.SelectedItem is not Peer { InputReady: false, Connected: false, BluetoothConnected: true } row ||
                !row.ConnectionDetail.StartsWith("Bluetooth 연결됨") || !row.ConnectionDetail.Contains("입력 연결 대기"))
                throw new InvalidOperationException("WinForms list conflates Bluetooth connection with input readiness.");
            Console.WriteLine("PASS: WinForms list clears HID readiness without erasing separately observed Bluetooth connection.");
        }
        finally
        {
            form._input.Dispose();
            form.EmergencyBleCleanup();
            form._timer.Dispose(); form._healthTimer.Dispose(); form._peerTimer.Dispose();
            form._tray.Dispose();
        }
    }
}

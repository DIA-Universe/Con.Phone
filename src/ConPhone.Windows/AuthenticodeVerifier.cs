using System.Runtime.InteropServices;

namespace ConPhone.Windows;

internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport(
        "wintrust.dll",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint WinVerifyTrust(
        IntPtr hwnd,
        ref Guid pgActionId,
        IntPtr pWinTrustData);

    public static bool IsValid(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        IntPtr pathPtr = IntPtr.Zero;
        IntPtr fileInfoPtr = IntPtr.Zero;
        IntPtr dataPtr = IntPtr.Zero;

        try
        {
            pathPtr = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));

            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                pcwszFilePath = pathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            fileInfoPtr = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(
                fileInfo,
                fileInfoPtr,
                fDeleteOld: false);

            var data = new WinTrustData
            {
                cbStruct = checked((uint)Marshal.SizeOf<WinTrustData>()),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_IGNORE,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                // Signature verification must not depend on network availability.
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero
            };

            dataPtr = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(
                data,
                dataPtr,
                fDeleteOld: false);

            var action = GenericVerifyV2;
            return WinVerifyTrust(
                IntPtr.Zero,
                ref action,
                dataPtr) == 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            OverflowException or
            System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(dataPtr);
            if (fileInfoPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPtr);
            if (pathPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pathPtr);
        }
    }
}

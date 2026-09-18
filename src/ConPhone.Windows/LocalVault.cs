using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ConPhone.Windows;
// Windows DPAPI, current-user scope; no passwords or machine-wide key in the source.
internal static class LocalVault
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    public static byte[] Seal(byte[] data)=>Transform(data,true);
    public static byte[] Unseal(byte[] data)=>Transform(data,false);
    private static byte[] Transform(byte[] data,bool encrypt)
    {
        Blob input=new() { Length=data.Length, Data=Marshal.AllocHGlobal(data.Length) }, output=default;
        try
        {
            Marshal.Copy(data,0,input.Data,data.Length);
            bool ok=encrypt ? CryptProtectData(ref input,null,0,0,0,1,out output) : CryptUnprotectData(ref input,0,0,0,0,1,out output);
            if(!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] result=new byte[output.Length]; Marshal.Copy(output.Data,result,0,result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if(output.Data!=0) LocalFree(output.Data); }
    }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob data,string? description,nint entropy,nint reserved,nint prompt,uint flags,out Blob result);
    [DllImport("crypt32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob data,nint description,nint entropy,nint reserved,nint prompt,uint flags,out Blob result);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

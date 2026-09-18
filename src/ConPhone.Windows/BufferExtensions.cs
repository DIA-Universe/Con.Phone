namespace ConPhone.Windows;

internal static class BufferExtensions
{
    // WinRT GATT offsets are uint; .NET span offsets are int.
    public static Span<byte> AsSpan(this byte[] bytes, uint offset) => bytes.AsSpan(checked((int)offset));
}

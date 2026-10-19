namespace ConPhone.Windows;

internal sealed class InputTransitionBlockedException(IReadOnlyList<int> keys)
    : InvalidOperationException($"전환 보류 · Windows가 눌림으로 감지한 입력: {Describe(keys)}. " +
        "표시된 키/버튼을 한 번 눌렀다 떼고 다시 전환하세요.")
{
    internal IReadOnlyList<int> Keys { get; } = keys.ToArray();

    internal static string Describe(IReadOnlyList<int> keys) => string.Join(", ", keys
        .Where(key => key switch {
            0x10 => !keys.Contains(0xA0) && !keys.Contains(0xA1),
            0x11 => !keys.Contains(0xA2) && !keys.Contains(0xA3),
            0x12 => !keys.Contains(0xA4) && !keys.Contains(0xA5),
            _ => true
        }).Select(key => key switch {
            0x01 => "왼쪽 마우스 버튼", 0x02 => "오른쪽 마우스 버튼",
            0x04 => "가운데 마우스 버튼", 0x05 => "마우스 추가 버튼 1", 0x06 => "마우스 추가 버튼 2",
            0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
            0xA0 => "왼쪽 Shift", 0xA1 => "오른쪽 Shift",
            0xA2 => "왼쪽 Ctrl", 0xA3 => "오른쪽 Ctrl",
            0xA4 => "왼쪽 Alt", 0xA5 => "오른쪽 Alt",
            0x5B => "왼쪽 Win", 0x5C => "오른쪽 Win",
            0x15 => "한/영 키", 0x19 => "한자 키",
            _ => HotkeyCatalog.NameOf(key)
        }));
}

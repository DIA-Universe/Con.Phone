using System.Diagnostics;
using ConPhone.Core;

namespace ConPhone.Windows;

public sealed partial class MainForm
{
    private Button _toggleButton = null!;
    private readonly Label _feedback = new();
    private readonly List<Button> _clipboardSelectionButtons = [];

    private void AddDisclosure(Control parent, string title, Control content)
    {
        content.Visible = false;
        var toggle = ActionButton(title + " ▾", () => Task.CompletedTask);
        toggle.Click += (_, _) =>
        {
            content.Visible = !content.Visible;
            toggle.Text = title + (content.Visible ? " ▴" : " ▾");
            if (parent is FlowLayoutPanel flow) ResizeSections(flow);
        };
        parent.Controls.Add(toggle);
        parent.Controls.Add(content);
    }

    private Button ClipboardSelectionButton(string text, Func<Task> action, bool primary = false)
    {
        var button = ActionButton(text, action, primary);
        button.Enabled = false;
        _clipboardSelectionButtons.Add(button);
        return button;
    }

    private static Task OpenBluetoothSettingsAsync()
    {
        Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private static void ResizeSections(FlowLayoutPanel flow)
    {
        if (flow.ClientSize.Width < 300) return;
        var width = Math.Max(300, flow.ClientSize.Width - flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        flow.SuspendLayout();
        foreach (Control card in flow.Controls)
        {
            card.MinimumSize = new Size(width, 0);
            card.MaximumSize = new Size(width, 0);
            card.Width = width;
            var inner = width - card.Padding.Horizontal - 8;
            foreach (Control child in card.Controls)
            {
                if (child is ListBox or TextBox) child.Width = inner;
                else if (child is Label label) label.MaximumSize = new Size(inner, 0);
                else if (child is FlowLayoutPanel row)
                {
                    row.MinimumSize = new Size(Math.Min(row.MinimumSize.Width, inner), 0);
                    row.MaximumSize = new Size(inner, 0);
                }
            }
        }
        flow.ResumeLayout();
    }

    private void ConfigureFriendlyLists()
    {
        _peers.DrawMode = DrawMode.OwnerDrawFixed;
        _peers.ItemHeight = 66;
        _peers.Height = 138;
        _peers.IntegralHeight = false;
        _peers.BorderStyle = BorderStyle.FixedSingle;
        _peers.AccessibleName = "페어링 및 등록된 기기 목록";
        _peers.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || _peers.Items[e.Index] is not Peer peer) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var brush = new SolidBrush(selected ? BrandWeak : Surface);
            e.Graphics.FillRectangle(brush, e.Bounds);
            using var titleFont = new Font(Font, FontStyle.Bold);
            var name = (peer.Active ? "●  " : "○  ") + peer.Name;
            var detail = !peer.Connected ? (peer.Paired
                    ? "Windows 페어링됨 · 휴대폰의 키보드·마우스 입력 연결 대기"
                    : "저장된 기기 · 현재 입력 연결 없음")
                : !peer.Keyboard ? "마우스 연결됨 · 키보드 입력 연결이 복구되지 않았어요"
                : !peer.Mouse ? "키보드 연결됨 · 마우스 입력 연결이 복구되지 않았어요"
                : peer.Active ? "현재 사용 기기 · 키보드와 마우스 준비됨"
                : peer.Trusted ? "등록된 기기 · 선택해서 전환할 수 있어요"
                : "새 기기 · ‘이 기기 사용’을 눌러 등록하세요";
            TextRenderer.DrawText(e.Graphics, name, titleFont,
                new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 9, e.Bounds.Width - 28, 24),
                selected ? BrandPressed : TextColor, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, detail, Font,
                new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 36, e.Bounds.Width - 28, 22),
                Secondary, TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
        _clipboardList.DrawMode = DrawMode.OwnerDrawFixed;
        _clipboardList.ItemHeight = 58;
        _clipboardList.Height = 180;
        _clipboardList.IntegralHeight = false;
        _clipboardList.AccessibleName = "최근 복사한 내용";
        _clipboardList.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || _clipboardList.Items[e.Index] is not ClipboardEntry entry) return;
            using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? BrandWeak : Surface);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, entry.Preview, Font,
                new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 7, e.Bounds.Width - 24, 23),
                TextColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(e.Graphics, $"{(entry.Kind == ClipboardEntryKind.Text ? "텍스트" : "이미지")}  ·  {entry.CapturedAt:MM/dd HH:mm}", Font,
                new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 31, e.Bounds.Width - 24, 21),
                Secondary, TextFormatFlags.SingleLine);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
    }
}

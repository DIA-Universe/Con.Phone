using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ConPhone.Core;

// CF_HTML is a local clipboard payload, never a URL to fetch or execute.
public static class HtmlClipboardText
{
    public const int MaxHtmlBytes = 4 * 1024 * 1024;
    public static string Extract(string value)
    {
        value = value.TrimEnd('\0');
        if (Encoding.UTF8.GetByteCount(value) > MaxHtmlBytes)
            throw new InvalidDataException("HTML 클립보드가 텍스트 변환 한도를 초과했습니다.");
        var fragment = Fragment(value);
        var doc = new HtmlDocument { OptionMaxNestedChildNodes = 128 };
        doc.LoadHtml(fragment);
        var output = new StringBuilder();
        void Break()
        {
            while (output.Length > 0 && output[^1] is ' ' or '\t') output.Length--;
            if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
        }
        void Visit(HtmlNode node, bool pre = false)
        {
            var name = node.Name.ToLowerInvariant();
            if (name is "script" or "style" or "head" or "template" or "iframe" or "object" or "svg") return;
            if (node.NodeType == HtmlNodeType.Comment) return;
            if (node.NodeType == HtmlNodeType.Text)
            {
                var text = WebUtility.HtmlDecode(((HtmlTextNode)node).Text).Replace('\u00a0', ' ');
                output.Append(pre ? text : Regex.Replace(text, @"[\t\r\n ]+", " "));
                return;
            }
            if (name == "br") { output.Append('\n'); return; }
            var block = name is "p" or "div" or "li" or "tr" or "blockquote" or "pre" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
            if (block) Break();
            foreach (var child in node.ChildNodes) Visit(child, pre || name == "pre");
            if (name is "td" or "th") output.Append('\t');
            if (block) Break();
        }
        Visit(doc.DocumentNode);
        var result = output.ToString().Trim();
        if (Encoding.UTF8.GetByteCount(result) > ClipboardContent.MaxTextBytes)
            throw new InvalidDataException("변환한 텍스트가 동기화 한도(1 MiB)를 초과했습니다.");
        return result;
    }

    private static string Fragment(string value)
    {
        // Offsets are UTF-8 bytes, not UTF-16 string indices (Korean/emoji differ).
        var headerEnd = value.IndexOf('<');
        var header = value[..(headerEnd < 0 ? Math.Min(value.Length, 4096) : Math.Min(headerEnd, 4096))];
        int Offset(string name) => int.TryParse(Regex.Match(header, $@"(?m)^{name}:\s*(\d+)").Groups[1].Value, out var n) ? n : -1;
        var start = Offset("StartFragment");
        var end = Offset("EndFragment");
        var bytes = Encoding.UTF8.GetBytes(value);
        if (start >= Encoding.UTF8.GetByteCount(header) && end > start && end <= bytes.Length)
            return new UTF8Encoding(false, true).GetString(bytes, start, end - start);
        var begin = Regex.Match(value, @"<!--\s*StartFragment\s*-->", RegexOptions.IgnoreCase);
        var finish = Regex.Match(value, @"<!--\s*EndFragment\s*-->", RegexOptions.IgnoreCase);
        if (begin.Success && finish.Success && finish.Index >= begin.Index + begin.Length)
            return value[(begin.Index + begin.Length)..finish.Index];
        // Never send CF_HTML metadata/SourceURL as copied text.
        if (header.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HTML 클립보드 선택 영역을 확인할 수 없습니다.");
        return value;
    }
}

using ConPhone.Core;

namespace ConPhone.Windows;

internal static class ClipboardTextReader
{
    public static string? Read(IDataObject? data, out bool convertedHtml)
    {
        convertedHtml = false;
        if (data is null) return null;
        // Rich sources may omit UnicodeText entirely (e.g. a Teams message copy).
        foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text })
            if (data.GetDataPresent(format) && data.GetData(format) is string text && text.Length > 0)
                return text;
        if (!data.GetDataPresent(DataFormats.Html, false)) return null;
        var payload = data.GetData(DataFormats.Html, false);
        var html = payload switch
        {
            string value => value,
            byte[] bytes when bytes.Length <= HtmlClipboardText.MaxHtmlBytes => new System.Text.UTF8Encoding(false, true).GetString(bytes),
            MemoryStream stream when stream.Length <= HtmlClipboardText.MaxHtmlBytes => new System.Text.UTF8Encoding(false, true).GetString(stream.ToArray()),
            _ => null
        };
        if (html is null) return null;
        convertedHtml = true;
        return HtmlClipboardText.Extract(html);
    }
}

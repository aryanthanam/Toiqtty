using Oroterm.Terminal;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace Oroterm.Controls;

internal static class TerminalDropData
{
    internal static Dictionary<string, byte[]> Offer(DataPackageView view)
    {
        var result = new Dictionary<string, byte[]>();
        if (view.Contains(StandardDataFormats.StorageItems)) result["text/uri-list"] = [];
        if (view.Contains(StandardDataFormats.Text)) result["text/plain"] = [];
        if (view.Contains(StandardDataFormats.Html)) result["text/html"] = [];
        if (view.Contains(StandardDataFormats.Rtf)) result["text/rtf"] = [];
        return result;
    }

    internal static async Task<(Dictionary<string, byte[]> Items, string? Fallback)> ReadAsync(DataPackageView view, string command)
    {
        var result = new Dictionary<string, byte[]>();
        string? fallback = null;
        int total = 0;
        void Add(string mime, string text)
        {
            int bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > TerminalClipboardLimits.MaxBytes - total) throw new ArgumentException("Drop exceeds the size limit.");
            total += bytes; result[mime] = Encoding.UTF8.GetBytes(text);
        }
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var items = await view.GetStorageItemsAsync();
            Add("text/uri-list", string.Join("\r\n", items.Select(item => new Uri(item.Path).AbsoluteUri)) + "\r\n");
            fallback = ShellIntegration.QuoteDroppedPaths(command, items.Select(item => item.Path));
        }
        if (view.Contains(StandardDataFormats.Text))
        {
            var text = await view.GetTextAsync(); Add("text/plain", text); fallback ??= text;
        }
        if (view.Contains(StandardDataFormats.Html)) Add("text/html", HtmlFormatHelper.GetStaticFragment(await view.GetHtmlFormatAsync()));
        if (view.Contains(StandardDataFormats.Rtf)) Add("text/rtf", await view.GetRtfAsync());
        return (result, fallback);
    }
}

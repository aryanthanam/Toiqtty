using System.Globalization;
using System.Text.Json;
using Lua;
using Toiqtty.Models;

namespace Toiqtty.Configuration;

public sealed record LuaTextSegment(string Text, string Foreground = "", bool Bold = false, bool Italic = false);
public sealed class LuaPresentationRequest
{
    public Dictionary<string, object?> Status { get; set; } = [];
    public List<Dictionary<string, object?>> Tabs { get; set; } = [];
    public string? Action { get; set; }
}
public sealed class LuaPresentationResult
{
    public Dictionary<string, LuaTextSegment[]> Titles { get; set; } = [];
    public LuaTextSegment[]? Left { get; set; }
    public LuaTextSegment[]? Right { get; set; }
    public string? Error { get; set; }
    public ActionSpec? Action { get; set; }
}

internal sealed class LuaConfigEvaluation(LuaState state, LuaConfigResult result, Dictionary<string, LuaValue> hooks)
{
    public LuaConfigResult Result { get; } = result;
    public async Task<LuaPresentationResult> Invoke(LuaPresentationRequest request)
    {
        if (request.Tabs.Count > 64 || request.Status.Count > 64) throw new ArgumentException("Runtime context exceeds its limit.");
        var output = new LuaPresentationResult();
        if (request.Action is { } name)
        {
            if (!hooks.TryGetValue("action:" + name, out var callback)) throw new ArgumentException("Unknown Lua action: " + name);
            var value = Single(await state.CallAsync(callback, [Context(request.Status)]));
            if (value.Type != LuaValueType.Nil) { output.Action = LuaConfigParser.ReadAction(value); output.Action.Validate(); }
            return output;
        }
        if (hooks.TryGetValue("format_tab_title", out var title)) foreach (var tab in request.Tabs)
        {
            var values = await state.CallAsync(title, [Context(tab)]);
            var segments = Segments(Single(values), "format_tab_title");
            if (segments is not null) output.Titles.Add(tab["id"]!.ToString()!, segments);
        }
        if (hooks.TryGetValue("format_status", out var status))
        {
            var value = Single(await state.CallAsync(status, [Context(request.Status)]));
            if (value.Type != LuaValueType.Nil)
            {
                if (value.Type != LuaValueType.Table) throw new ArgumentException("format_status must return {left=...,right=...} or nil.");
                var table = value.Read<LuaTable>();
                foreach (var pair in table) if (pair.Key.Type != LuaValueType.String || pair.Key.Read<string>() is not ("left" or "right")) throw new ArgumentException("Unknown format_status field.");
                output.Left = Segments(table["left"], "format_status.left"); output.Right = Segments(table["right"], "format_status.right");
            }
        }
        return output;
    }
    private static LuaValue Single(LuaValue[] values) => values.Length == 0 ? LuaValue.Nil : values.Length == 1 ? values[0] : throw new ArgumentException("Runtime hooks must return at most one value.");
    private static LuaValue Context(Dictionary<string, object?> values) => FromJson(JsonSerializer.SerializeToElement(values), 0);
    private static LuaValue FromJson(JsonElement value, int depth)
    {
        if (depth > 4) throw new ArgumentException("Runtime context is too deep.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var table = new LuaTable(); foreach (var pair in value.EnumerateObject()) table[pair.Name] = FromJson(pair.Value, depth + 1); return table;
        }
        return value.ValueKind switch { JsonValueKind.String => new LuaValue(value.GetString()!), JsonValueKind.Number => new LuaValue(value.GetDouble()),
            JsonValueKind.True => new LuaValue(true), JsonValueKind.False => new LuaValue(false), _ => LuaValue.Nil };
    }
    private static LuaTextSegment[]? Segments(LuaValue value, string name)
    {
        if (value.Type == LuaValueType.Nil) return null;
        var segments = new List<LuaTextSegment>();
        void Add(LuaValue item)
        {
            if (item.Type == LuaValueType.String) { segments.Add(new(Text(item))); return; }
            if (item.Type != LuaValueType.Table) throw new ArgumentException(name + " segments must be strings or styled text tables.");
            var table = item.Read<LuaTable>();
            foreach (var pair in table) if (pair.Key.Type != LuaValueType.String || pair.Key.Read<string>() is not ("text" or "foreground" or "bold" or "italic")) throw new ArgumentException("Unknown styled text field.");
            string color = table["foreground"].Type == LuaValueType.Nil ? "" : Text(table["foreground"]);
            if (color.Length != 0 && !SessionTheme.TryColor(color, out _)) throw new ArgumentException("Styled text foreground must be #RRGGBB.");
            bool Flag(string key) => table[key].Type != LuaValueType.Nil && (table[key].Type == LuaValueType.Boolean ? table[key].Read<bool>() : throw new ArgumentException(key + " must be boolean."));
            segments.Add(new(Text(table["text"]), color, Flag("bold"), Flag("italic")));
        }
        if (value.Type == LuaValueType.String) Add(value);
        else if (value.Type == LuaValueType.Table)
        {
            var table = value.Read<LuaTable>();
            if (table.ArrayLength > 16 || table.HashMapCount > 0 || table.Count() != table.ArrayLength) throw new ArgumentException(name + " must be a contiguous array of at most 16 segments.");
            for (int i = 1; i <= table.ArrayLength; i++) Add(table[i]);
        }
        else throw new ArgumentException(name + " must return a string, text segments, or nil.");
        return segments.ToArray();
    }
    private static string Text(LuaValue value)
    {
        if (value.Type != LuaValueType.String) throw new ArgumentException("Runtime text must be a string.");
        string text = value.Read<string>();
        if (text.Length > 1024 || text.Any(char.IsControl)) throw new ArgumentException("Runtime text must be a single line of at most 1024 characters.");
        return text;
    }
    internal static string Strftime(string format)
    {
        if (format.Length > 128) throw new ArgumentException("strftime format is too long.");
        var now = DateTimeOffset.Now; var result = new System.Text.StringBuilder();
        for (int i=0; i<format.Length; i++)
        {
            if (format[i] != '%') { result.Append(format[i]); continue; }
            if (++i == format.Length) throw new ArgumentException("Incomplete strftime token.");
            string? token = format[i] switch { 'a'=>"ddd", 'A'=>"dddd", 'b'=>"MMM", 'B'=>"MMMM", 'd'=>"dd", 'm'=>"MM", 'Y'=>"yyyy", 'y'=>"yy", 'H'=>"HH", 'I'=>"hh", 'M'=>"mm", 'S'=>"ss", 'p'=>"tt", 'z'=>"zzz", '%'=>null, _=>throw new ArgumentException("Unsupported strftime token.") };
            result.Append(token is null ? "%" : format[i]=='z' ? now.ToString(token, CultureInfo.InvariantCulture).Replace(":","") : now.ToString(token, CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
}

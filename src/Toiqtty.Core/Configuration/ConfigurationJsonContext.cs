using System.Text.Json;
using System.Text.Json.Serialization;
using Toiqtty.Models;

namespace Toiqtty.Configuration;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SavedSession))]
[JsonSerializable(typeof(List<Workspace>))]
[JsonSerializable(typeof(LuaConfigRequest))]
[JsonSerializable(typeof(LuaConfigResult))]
[JsonSerializable(typeof(LuaPresentationRequest))]
[JsonSerializable(typeof(LuaPresentationResult))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(JsonElement))]
internal partial class ConfigurationJsonContext : JsonSerializerContext
{
    internal static ConfigurationJsonContext Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}

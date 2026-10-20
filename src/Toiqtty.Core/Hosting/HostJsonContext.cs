using System.Text.Json.Serialization;

namespace Toiqtty.Hosting;

[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostReply))]
internal partial class HostJsonContext : JsonSerializerContext;

using Iris_MediaPlayer.Models;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// Compile-time JSON metadata for everything Iris saves. Release builds are trimmed, which turns
/// off reflection-based System.Text.Json, so every Serialize/Deserialize call must pass one of
/// these type infos (for example <c>IrisJsonContext.Default.ListString</c>).
/// To save a new type, add a [JsonSerializable] line here.
/// </summary>
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<MediaCollection>))]
[JsonSerializable(typeof(Dictionary<string, PlaybackProgressEntry>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, TrackChoice>))]
internal sealed partial class IrisJsonContext : JsonSerializerContext
{
}

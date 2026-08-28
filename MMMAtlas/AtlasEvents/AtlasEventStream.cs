using System.Text.Json;

namespace MMMAtlas.AtlasEvents;

public static class AtlasEventStream
{
    public const string Heartbeat = ": heartbeat\n\n";

    public static string Format(AtlasEvent atlasEvent)
    {
        ArgumentNullException.ThrowIfNull(atlasEvent);

        return $"event: {atlasEvent.Name}\n"
            + $"data: {JsonSerializer.Serialize(new { gameId = atlasEvent.GameId })}\n\n";
    }
}

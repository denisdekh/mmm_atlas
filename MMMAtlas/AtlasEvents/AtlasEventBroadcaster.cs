using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MMMAtlas.AtlasEvents;

public sealed record AtlasEvent(string Name, long GameId)
{
    public const string GameUpdatedName = "game-updated";

    public static AtlasEvent GameUpdated(long gameId) =>
        new(GameUpdatedName, gameId);
}

public sealed class AtlasEventBroadcaster
{
    private const int SubscriberCapacity = 32;
    private readonly ConcurrentDictionary<Guid, Channel<AtlasEvent>>
        _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public AtlasEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AtlasEvent>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        if (!_subscribers.TryAdd(id, channel))
        {
            throw new InvalidOperationException(
                "Could not register the atlas event subscriber.");
        }

        return new AtlasEventSubscription(
            channel.Reader,
            () => RemoveSubscriber(id));
    }

    public void PublishGameUpdated(long gameId)
    {
        if (gameId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameId));
        }

        var atlasEvent = AtlasEvent.GameUpdated(gameId);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(atlasEvent);
        }
    }

    private void RemoveSubscriber(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}

public sealed class AtlasEventSubscription : IDisposable
{
    private Action? _unsubscribe;

    internal AtlasEventSubscription(
        ChannelReader<AtlasEvent> reader,
        Action unsubscribe)
    {
        Reader = reader;
        _unsubscribe = unsubscribe;
    }

    public ChannelReader<AtlasEvent> Reader { get; }

    public void Dispose() =>
        Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}

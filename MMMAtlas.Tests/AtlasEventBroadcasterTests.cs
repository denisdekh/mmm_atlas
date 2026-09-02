using MMMAtlas.AtlasEvents;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class AtlasEventBroadcasterTests
{
    [TestMethod]
    public async Task PublishDeliversToConcurrentSubscribers()
    {
        var broadcaster = new AtlasEventBroadcaster();
        using var first = broadcaster.Subscribe();
        using var second = broadcaster.Subscribe();

        broadcaster.PublishGameUpdated(123);

        Assert.AreEqual(
            AtlasEvent.GameUpdated(123),
            await first.Reader.ReadAsync());
        Assert.AreEqual(
            AtlasEvent.GameUpdated(123),
            await second.Reader.ReadAsync());
    }

    [TestMethod]
    public void DisposingSubscriptionRemovesItFromBroadcaster()
    {
        var broadcaster = new AtlasEventBroadcaster();
        var subscription = broadcaster.Subscribe();
        Assert.AreEqual(1, broadcaster.SubscriberCount);

        subscription.Dispose();
        subscription.Dispose();

        Assert.AreEqual(0, broadcaster.SubscriberCount);
    }

    [TestMethod]
    public void GameUpdatedUsesSseEventAndJsonDataFields()
    {
        var formatted = AtlasEventStream.Format(AtlasEvent.GameUpdated(123));

        Assert.AreEqual(
            "event: game-updated\ndata: {\"gameId\":123}\n\n",
            formatted);
    }

    [TestMethod]
    public async Task SlowSubscriberDropsOldEventsWithoutBlockingPublisher()
    {
        var broadcaster = new AtlasEventBroadcaster();
        using var subscription = broadcaster.Subscribe();

        for (var gameId = 1; gameId <= 100; gameId++)
        {
            broadcaster.PublishGameUpdated(gameId);
        }

        var received = new List<long>();
        while (subscription.Reader.TryRead(out var atlasEvent))
        {
            received.Add(atlasEvent.GameId);
        }

        Assert.HasCount(32, received);
        Assert.AreEqual(69L, received[0]);
        Assert.AreEqual(100L, received[^1]);
        await Task.CompletedTask;
    }
}

using MMMAtlas.Steam;

namespace MMMAtlas.Tests;

internal sealed class TestStoreParentClient : ISteamStoreParentClient
{
    public HashSet<int> ParentAppIds { get; } = [4,5,6,7,8];
    public Task<IReadOnlyDictionary<int, bool>?> GetParentsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, bool>?>(appIds.ToDictionary(id => id, ParentAppIds.Contains));
}

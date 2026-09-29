using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteLLM.Core;

public sealed class ModelCatalog
{
    private readonly Dictionary<Guid, CacheEntry> _cache = new Dictionary<Guid, CacheEntry>();
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(15);
    public void Put(Guid serverId, IEnumerable<ModelInfo> models) => _cache[serverId] = new CacheEntry(DateTimeOffset.UtcNow, models.ToList());
    public IReadOnlyList<ModelInfo> GetAll(IEnumerable<Guid> enabledServers) => enabledServers.Where(id => _cache.ContainsKey(id)).SelectMany(id => _cache[id].Models).OrderBy(m => m.Id).ThenBy(m => m.ServerName).ToList();
    public bool IsFresh(Guid serverId) => _cache.TryGetValue(serverId, out var item) && DateTimeOffset.UtcNow - item.Timestamp < Lifetime;
    public ModelInfo? Match(string key) => _cache.Values.SelectMany(x => x.Models).FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
    private sealed class CacheEntry { public CacheEntry(DateTimeOffset timestamp, List<ModelInfo> models) { Timestamp = timestamp; Models = models; } public DateTimeOffset Timestamp { get; } public List<ModelInfo> Models { get; } }
}

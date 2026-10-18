using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>The collected pages of a list call, and whether a cap cut the list short. A truncated list is never
/// returned as if it were complete: <see cref="Truncated"/> and <see cref="Note"/> exist so the transcript can say
/// what was left out.</summary>
public sealed class McpPage
{
    public McpPage(List<JToken> items, int pages, bool truncated, string? note)
    {
        Items = items;
        Pages = pages;
        Truncated = truncated;
        Note = note;
    }

    public IReadOnlyList<JToken> Items { get; }
    public int Pages { get; }
    public bool Truncated { get; }

    /// <summary>Null unless a cap cut the list; otherwise the sentence that names the cap.</summary>
    public string? Note { get; }
}

/// <summary>Follows <c>nextCursor</c> until a page has none. Both the page count and the item count are capped: a
/// server that keeps handing out a cursor must not be able to keep a turn running, and the cap that was reached is
/// reported rather than silently producing a shorter list.</summary>
public static class McpPagination
{
    public const int DefaultMaxPages = 20;
    public const int DefaultMaxItems = 1000;

    public static async Task<McpPage> CollectAsync(JObject first, string itemsProperty,
        Func<string, CancellationToken, Task<JObject>> fetchNext,
        int maxItems = DefaultMaxItems, int maxPages = DefaultMaxPages,
        CancellationToken cancellationToken = default)
    {
        var items = new List<JToken>();
        var page = first;
        var pages = 0;

        while (true)
        {
            pages++;
            if (page[itemsProperty] is JArray array)
            {
                foreach (var item in array)
                {
                    if (items.Count >= maxItems)
                        return new McpPage(items, pages, true, "only the first " + maxItems + " items were read");
                    items.Add(item.DeepClone());
                }
            }

            var cursor = page["nextCursor"]?.Type == JTokenType.String ? page["nextCursor"]!.Value<string>() : null;
            if (string.IsNullOrEmpty(cursor)) return new McpPage(items, pages, false, null);
            if (pages >= maxPages)
                return new McpPage(items, pages, true, "only the first " + maxPages + " pages were read");

            cancellationToken.ThrowIfCancellationRequested();
            page = await fetchNext(cursor!, cancellationToken).ConfigureAwait(false);
        }
    }
}

using Recrovit.AI.Core;
using Recrovit.AI.Core.Conversations.History;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Workflows;
using Recrovit.RecroGridFramework.Metadata;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.legacy.rgf-ai.entity-conversation
// Search terms, identities and digests support revalidation; authoritative history stays in Recrovit.AI.
internal sealed class RgfRecrobyMetadataDisclosures(IConversationHistoryReader history)
{
    private readonly ConcurrentDictionary<string, DisclosureSet> conversations = new(StringComparer.Ordinal);
    private long nextCleanup;

    internal void Record(string conversationId, string userId, int entityId, bool detailed, object value, string? search = null)
    {
        var set = conversations.GetOrAdd(conversationId, _ => new(userId));
        if (set.UserId != userId) throw new UnauthorizedAccessException("Metadata conversation owner mismatch.");
        set.Items.TryAdd(new(entityId, detailed, Digest(value), search ?? string.Empty), 0);
    }

    internal IReadOnlyList<Disclosure> Read(string conversationId, string? userId)
    {
        Cleanup();
        if (!conversations.TryGetValue(conversationId, out var set)) return [];
        if (set.UserId != userId) throw new UnauthorizedAccessException("Metadata conversation owner mismatch.");
        return set.Items.Keys.ToArray();
    }

    internal static string Digest(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private void Cleanup()
    {
        var now = Environment.TickCount64;
        var next = Volatile.Read(ref nextCleanup);
        if (now < next || Interlocked.CompareExchange(ref nextCleanup, now + 60000, next) != next) return;
        foreach (var id in conversations.Keys)
        {
            try { _ = history.Read(id); }
            catch (KeyNotFoundException) { conversations.TryRemove(id, out _); }
        }
    }

    // EntityId zero identifies a search-result count, including an empty page with no Entity summaries.
    internal sealed record Disclosure(int EntityId, bool Detailed, string Digest, string Search);
    private sealed class DisclosureSet(string userId)
    {
        internal string UserId { get; } = userId;
        internal ConcurrentDictionary<Disclosure, byte> Items { get; } = new();
    }
}

internal sealed class RgfRecrobyMetadataHistoryGuard(RgfRecrobyMetadataDisclosures disclosures,
    RgfRecrobyMetadataAccess access) : IAIProviderRequestGuard
{
    internal const string RejectionCode = "RgfRecroby.MetadataHistoryUnavailable";
    internal const string RejectionMessage = "Previous metadata access could not be verified. Start a new Recroby conversation.";

    internal static bool IsRejection(Exception exception)
        => exception is AIProviderRequestRejectedException { ReasonCode: RejectionCode }
            || (exception.InnerException is { } inner && IsRejection(inner))
            || (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsRejection));

    public async ValueTask BeforeRequestAsync(AIProviderRequestContext context, CancellationToken cancellationToken = default)
    {
        if (context.WorkflowId != "rgf.recroby") return;
        if (string.IsNullOrWhiteSpace(context.ConversationId)) return;
        var host = context.HostContext as RgfRecrobyContext;
        var previous = disclosures.Read(context.ConversationId, host?.UserId);
        if (previous.Count == 0) return;
        try
        {
            await access.UseAsync(host!, async (metadata, rgContext) =>
            {
                var searches = new Dictionary<string, IReadOnlyList<RgfEntityCatalogSummary>>(StringComparer.Ordinal);
                foreach (var search in previous.Where(p => !p.Detailed).Select(p => p.Search).Distinct(StringComparer.Ordinal))
                    searches.Add(search, (await metadata.SearchCatalogAsync(rgContext, search, cancellationToken))
                        .Where(e => e.EntityId > 10000).ToArray());
                foreach (var group in previous.GroupBy(p => (p.EntityId, p.Detailed, p.Search)))
                {
                    object? current = group.Key.Detailed
                        ? await metadata.ReadAsync(rgContext, group.Key.EntityId, cancellationToken)
                        : group.Key.EntityId == 0 ? searches[group.Key.Search].Count
                        : searches[group.Key.Search].SingleOrDefault(e => e.EntityId == group.Key.EntityId);
                    if (current is null || group.Any(p => p.Digest != RgfRecrobyMetadataDisclosures.Digest(current)))
                        throw new UnauthorizedAccessException("Previously disclosed metadata is no longer verifiable.");
                }
                return true;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AIProviderRequestRejectedException(
                RejectionMessage, RejectionCode);
        }
    }
}

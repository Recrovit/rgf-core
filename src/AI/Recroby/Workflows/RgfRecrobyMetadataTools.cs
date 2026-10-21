using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recrovit.AI.Core.Workflows;
using Recrovit.RecroGridFramework.Identity;
using Recrovit.RecroGridFramework.Metadata;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Workflows;

// RGF-DOC: rgf.legacy.rgf-ai.entity-conversation
internal static class RgfRecrobyMetadataTools
{
    internal const string SearchToolId = "rgf.recroby.metadata.catalog.search";
    internal const string ReadToolId = "rgf.recroby.metadata.entity.read";
    private const string SearchInput = SearchToolId + ".input";
    private const string SearchOutput = SearchToolId + ".output";
    private const string ReadInput = ReadToolId + ".input";
    private const string ReadOutput = ReadToolId + ".output";

    internal static void RegisterContracts(RecrovitWorkflowConfiguration configuration)
    {
        Register<CatalogSearch>(SearchInput);
        Register<CatalogPage>(SearchOutput);
        Register<EntityRead>(ReadInput);
        Register<RgfEntityMetadataResult>(ReadOutput);
        void Register<T>(string id)
        {
            if (!configuration.Contracts.TryGet(id, out _)) configuration.Contracts.Register<T>(id);
        }
    }

    internal static void Register(RecrovitWorkflowConfiguration configuration, IServiceScopeFactory scopes)
    {
        RegisterContracts(configuration);
        configuration.Tools.Register(new ApplicationToolDefinition
        {
            Id = SearchToolId, InputContractId = SearchInput, OutputContractId = SearchOutput,
            Description = "Search authorized application Entity summaries by text (not semantic ranking). " +
                "Empty search browses all Entities. Offset starts at zero; page size is 1 to 100. " +
                "Follow NextOffset for more matches. Read details to verify Properties."
        }, async (call, input, ct) =>
        {
            ValidateCall(call, SearchToolId);
            var search = input?.ContractId == SearchInput ? configuration.Contracts.Deserialize(input) as CatalogSearch : null;
            if (search is null || search.Offset < 0 || search.PageSize is < 1 or > 100)
                throw new ArgumentException("Expected search, nonnegative offset and page size between 1 and 100.");
            await using var scope = scopes.CreateAsyncScope();
            var access = scope.ServiceProvider.GetRequiredService<RgfRecrobyMetadataAccess>();
            return await access.UseAsync((RgfRecrobyContext)call.HostContext!, async (metadata, context) =>
            {
                var all = (await metadata.SearchCatalogAsync(context, search.Search, ct))
                    .Where(e => e.EntityId > 10000).OrderBy(e => e.EntityId).ToArray();
                var entries = all.Skip(search.Offset).Take(search.PageSize).ToArray();
                var next = (long)search.Offset + entries.Length;
                var page = new CatalogPage(entries, all.Length, next < all.Length ? (int)next : null);
                var disclosures = scope.ServiceProvider.GetRequiredService<RgfRecrobyMetadataDisclosures>();
                disclosures.Record(call.ConversationId, ((RgfRecrobyContext)call.HostContext!).UserId,
                    0, false, all.Length, search.Search);
                foreach (var entry in entries) disclosures.Record(call.ConversationId,
                    ((RgfRecrobyContext)call.HostContext!).UserId, entry.EntityId, false, entry, search.Search);
                return new ApplicationToolResult { Output = configuration.Contracts.Serialize(SearchOutput, page) };
            }, ct);
        });
        configuration.Tools.Register(new ApplicationToolDefinition
        {
            Id = ReadToolId, InputContractId = ReadInput, OutputContractId = ReadOutput,
            Description = "Read freshly authorized Entity/Property metadata, types, relations and business " +
                "descriptions by EntityId above 10000. Returns no business records."
        }, async (call, input, ct) =>
        {
            ValidateCall(call, ReadToolId);
            var read = input?.ContractId == ReadInput ? configuration.Contracts.Deserialize(input) as EntityRead : null;
            if (read is null) throw new ArgumentException("An EntityId is required.");
            await using var scope = scopes.CreateAsyncScope();
            var access = scope.ServiceProvider.GetRequiredService<RgfRecrobyMetadataAccess>();
            return await access.UseAsync((RgfRecrobyContext)call.HostContext!, async (metadata, context) =>
            {
                var result = read.EntityId > 10000
                    ? await metadata.ReadAsync(context, read.EntityId, ct)
                    : new RgfEntityMetadataResult(RgfMetadataReadStatus.Unavailable, null!, []);
                if (result.Status == RgfMetadataReadStatus.Available && result.Entity is not null)
                    scope.ServiceProvider.GetRequiredService<RgfRecrobyMetadataDisclosures>().Record(
                        call.ConversationId, ((RgfRecrobyContext)call.HostContext!).UserId, read.EntityId, true, result);
                return new ApplicationToolResult { Output = configuration.Contracts.Serialize(ReadOutput, result) };
            }, ct);
        });
    }

    private static void ValidateCall(ApplicationToolContext call, string toolId)
    {
        if (call.WorkflowId != "rgf.recroby" || call.AgentId != "rgf.recroby.application" ||
            call.ToolId != toolId || call.HostContext is not RgfRecrobyContext ||
            string.IsNullOrWhiteSpace(call.ConversationId) || string.IsNullOrWhiteSpace(call.WorkflowRunId))
            throw new UnauthorizedAccessException("Metadata tools require the Recroby application agent and trusted host context.");
    }

    private sealed record CatalogSearch(string? Search = null, int Offset = 0, int PageSize = 50);
    private sealed record EntityRead(int EntityId);
    private sealed record CatalogPage(IReadOnlyList<RgfEntityCatalogSummary> Entities, int TotalCount, int? NextOffset);
}

internal sealed class RgfRecrobyMetadataAccess(RgfRecrobyEntityMetadataService metadata,
    IRgfIdentityService identity, IHttpContextAccessor http, ILogger<RgfRecrobyMetadataAccess> logger)
{
    internal async Task<T> UseAsync<T>(RgfRecrobyContext host,
        Func<RgfRecrobyEntityMetadataService, IRecroGridContext, Task<T>> action, CancellationToken ct)
    {
        var authenticated = await RgfAiIdentityResolver.ResolveAsync(identity, http, ct);
        var current = http.HttpContext;
        if (authenticated is null || authenticated.UserId != host.UserId || current?.User.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Metadata access requires the authenticated RGF HTTP context.");
        await using var context = new RecroGridContext(new RGFLogger(logger), current) { UseAsyncInitialization = true };
        try { return await action(metadata, context); }
        finally { context.ServiceScope.Dispose(); }
    }
}

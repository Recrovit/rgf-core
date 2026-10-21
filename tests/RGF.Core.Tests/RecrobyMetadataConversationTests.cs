using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core.Workflows;
using Recrovit.RecroGridFramework;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Http;
using Recrovit.RecroGridFramework.Metadata;
using Rgf.Metadata.Tests;
using Xunit;

namespace RGF.Core.Tests;

public sealed partial class RecrobyTests
{
    private const string SearchId = "rgf.recroby.metadata.catalog.search";
    private const string ReadId = "rgf.recroby.metadata.entity.read";

    [Fact]
    public async Task ExistingEndpointSupportsSuccessiveNativeToolsAcrossIndependentEntitiesAndFollowups()
    {
        using var host = new MetadataTestFixture();
        using var configuration = new MetadataConfigurationFixture(host);
        configuration.Entity(10001, "Customers", typeof(ConversationRecord));
        configuration.UpdateEntity(10001, "EntityComment", "Customer management business description");
        configuration.Property(10001, 1001, "ExpiryDate");
        configuration.Entity(10002, "Approvals", typeof(ConversationRecord));
        configuration.Property(10002, 2001, "ApprovalStatus");
        configuration.Entity(10003, "Orders", typeof(ConversationRecord));
        configuration.Property(10003, 3001, "ExpiryDate");
        await using var fixture = new Fixture(metadataReader: configuration.Reader);
        fixture.DefaultChat.DecisionKeys.Enqueue("application");
        var calls = fixture.DefaultChat.MetadataCalls;
        calls.Enqueue(("rgf_recroby_metadata_catalog_search", new() { ["search"] = "", ["offset"] = 0, ["pageSize"] = 1 }));
        calls.Enqueue(("rgf_recroby_metadata_catalog_search", new() { ["search"] = "Customer management" }));
        calls.Enqueue(("rgf_recroby_metadata_entity_read", new() { ["entityId"] = 10001 }));
        calls.Enqueue(("rgf_recroby_metadata_catalog_search", new() { ["search"] = "ApprovalStatus" }));
        calls.Enqueue(("rgf_recroby_metadata_entity_read", new() { ["entityId"] = 10002 }));
        calls.Enqueue(("rgf_recroby_metadata_entity_read", new() { ["entityId"] = 10003 }));
        var controller = new RgfRecrobyController(fixture.Service, fixture.Identity)
        {
            ControllerContext = new() { HttpContext = fixture.Http.HttpContext! }
        };
        var response = Assert.IsType<RgfAiResponse>(Assert.IsType<OkObjectResult>(
            (await controller.ExecuteAsync(new() { CurrentUserMessage = "Find customer management, expiry and approvals." }, Token)).Result).Value);
        Assert.True(response.Success);
        Assert.Empty(calls);
        var results = ToolResults(fixture.DefaultChat).ToArray();
        Assert.Contains(results, r => r.TryGetProperty("totalCount", out var total) && total.GetInt32() == 3
            && r.GetProperty("nextOffset").GetInt32() == 1 && r.GetProperty("entities").GetArrayLength() == 1);
        Assert.Contains(results, r => r.TryGetProperty("entities", out var entries) && entries.GetArrayLength() == 1
            && entries[0].GetProperty("entityName").GetString() == "Approvals");
        foreach (var id in new[] { 10001, 10002, 10003 })
            Assert.Contains(results, r => r.TryGetProperty("entity", out var entity) && entity.GetProperty("entityId").GetInt32() == id);
        fixture.DefaultChat.DecisionKeys.Enqueue("application");
        calls.Enqueue(("rgf_recroby_metadata_entity_read", new() { ["entityId"] = 10001 }));
        var followup = await fixture.Service.ExecuteAsync("metadata-test-owner", Reply(response, "What Properties does the first one have?"), Token);
        Assert.True(followup.Success);
        Assert.Equal(response.ConversationId, followup.ConversationId);
        Assert.Contains(fixture.DefaultChat.Messages.Last(), m => m.Role == ChatRole.User && m.Text.Contains("Find customer management"));
        Assert.Equal(0, configuration.Guard.WritesDuringRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RevokedMetadataStopsContinuationBeforeIntentReceivesAnyHistory(bool property, bool summarySearch)
    {
        using var host = new MetadataTestFixture();
        using var configuration = new MetadataConfigurationFixture(host);
        configuration.Entity(10001, "Customers", typeof(ConversationRecord));
        configuration.Option(10001, "RGO_RecroSecLevel", "row,column");
        configuration.Property(10001, 1001, "ExpiryDate");
        await using var fixture = new Fixture(metadataReader: configuration.Reader);
        fixture.DefaultChat.DecisionKeys.Enqueue("application");
        fixture.DefaultChat.MetadataCalls.Enqueue((property && !summarySearch ? "rgf_recroby_metadata_entity_read" : "rgf_recroby_metadata_catalog_search",
            property && !summarySearch ? new() { ["entityId"] = 10001 } : new() { ["search"] = summarySearch ? "ExpiryDate" : "" }));
        var first = await fixture.Service.ExecuteAsync("metadata-test-owner", new() { CurrentUserMessage = "Describe Customers" }, Token);
        Assert.True(first.Success);
        var callsBefore = fixture.DefaultChat.Messages.Count;
        configuration.Grant(property ? "Customers/ExpiryDate" : "Customers", false);
        var next = await fixture.Service.ExecuteAsync("metadata-test-owner", Reply(first, "Tell me again"), Token);
        Assert.False(next.Success);
        Assert.Equal("RgfRecroby.MetadataHistoryUnavailable", next.ErrorCode);
        Assert.Contains("new Recroby conversation", next.Message);
        Assert.Equal(callsBefore, fixture.DefaultChat.Messages.Count);
    }

    [Fact]
    public async Task CatalogPagesAreCompleteDeterministicAndDoNotReadEveryEntity()
    {
        var reader = new LargeCatalogReader();
        await using var fixture = new Fixture(metadataReader: reader);
        var config = fixture.Configuration;
        var ids = new List<int>();
        int? offset = 0;
        while (offset is not null)
        {
            var result = await CallTool(config, SearchId, new { offset, pageSize = 100 });
            Assert.Equal(205, result.GetProperty("totalCount").GetInt32());
            ids.AddRange(result.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("entityId").GetInt32()));
            offset = result.GetProperty("nextOffset").GetInt32Nullable();
        }
        Assert.Equal(Enumerable.Range(10001, 205), ids);
        Assert.Equal(0, reader.DetailCalls);
        var unavailable = await CallTool(config, ReadId, new { entityId = 10000 });
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("entity").ValueKind);
        Assert.Equal(0, reader.DetailCalls);
    }

    [Theory]
    [InlineData("other", "rgf.recroby.application", "metadata-test-owner", false)]
    [InlineData("rgf.recroby", "rgf.recroby.chat", "metadata-test-owner", false)]
    [InlineData("rgf.recroby", "rgf.recroby.application", "forged", false)]
    [InlineData("rgf.recroby", "rgf.recroby.application", "missing-host", false)]
    [InlineData("rgf.recroby", "rgf.recroby.application", "metadata-test-owner", true)]
    public async Task ToolsRejectWrongWorkflowAgentOwnerAndMissingHttpContext(string workflow, string agent, string user, bool missingHttp)
    {
        var reader = new LargeCatalogReader();
        await using var fixture = new Fixture(metadataReader: reader);
        if (missingHttp) fixture.Http.HttpContext = null;
        var config = fixture.Configuration;
        foreach (var id in new[] { SearchId, ReadId })
        {
            Assert.True(config.Tools.TryGet(id, out var definition, out var handler));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await handler!(new()
            {
                WorkflowId = workflow, AgentId = agent, ToolId = id, ConversationId = "test", WorkflowRunId = "run",
                HostContext = user == "missing-host" ? null : new RgfRecrobyContext(user)
            }, new() { ContractId = definition!.InputContractId!, Value = JsonSerializer.SerializeToElement(new { entityId = 10001 }) }, Token));
        }
        Assert.Equal(0, reader.DetailCalls);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task CatalogRejectsInvalidPagingInsteadOfSilentlyTruncating(int offset, int pageSize)
    {
        await using var fixture = new Fixture(metadataReader: new LargeCatalogReader());
        await Assert.ThrowsAsync<ArgumentException>(() => CallTool(fixture.Configuration, SearchId, new { offset, pageSize }));
    }

    [Fact]
    public async Task MissingOrDeniedDetailsAndReaderErrorsDoNotDiscloseMetadata()
    {
        var reader = new LargeCatalogReader();
        await using var fixture = new Fixture(metadataReader: reader);
        var denied = await CallTool(fixture.Configuration, ReadId, new { entityId = 99999 });
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("entity").ValueKind);
        fixture.DefaultChat.DecisionKeys.Enqueue("application");
        fixture.DefaultChat.MetadataCalls.Enqueue(("rgf_recroby_metadata_entity_read", new() { ["entityId"] = 10001 }));
        reader.Failure = new InvalidOperationException("private configuration failure");
        var result = await fixture.Service.ExecuteAsync("metadata-test-owner", new() { CurrentUserMessage = "Read metadata" }, Token);
        Assert.False(result.Success);
        Assert.DoesNotContain("private configuration", result.Message);
        Assert.Equal(2, fixture.DefaultChat.Messages.Count);
    }

    private static async Task<JsonElement> CallTool(RecrovitWorkflowConfiguration config, string id, object input)
    {
        Assert.True(config.Tools.TryGet(id, out var definition, out var handler));
        var result = await handler!(new()
        {
            WorkflowId = "rgf.recroby", AgentId = "rgf.recroby.application", ToolId = id,
            ConversationId = "catalog-test", WorkflowRunId = "run", HostContext = new RgfRecrobyContext("metadata-test-owner")
        }, new() { ContractId = definition!.InputContractId!, Value = JsonSerializer.SerializeToElement(input) }, Token);
        return result.Output.Value;
    }

    private static IEnumerable<JsonElement> ToolResults(ChatClient client) => client.Messages.SelectMany(m => m)
        .SelectMany(m => m.Contents).OfType<FunctionResultContent>()
        .Select(r => r.Result is JsonElement json ? json : JsonSerializer.SerializeToElement(r.Result));

    public sealed class ConversationRecord
    {
        public DateTime? ExpiryDate { get; set; }
        public string? ApprovalStatus { get; set; }
    }

    private sealed class LargeCatalogReader : IRgfEntityMetadataReader
    {
        internal int DetailCalls;
        internal Exception? Failure;
        public Task<IReadOnlyList<RgfEntityCatalogSummary>> SearchCatalogAsync(IRecroGridContext context, string? search = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RgfEntityCatalogSummary>>(
                Enumerable.Range(10000, 206).Reverse().Select(id => new RgfEntityCatalogSummary(id, $"Entity{id}", "1", "Title", "Description")).ToArray());
        public Task<RgfEntityMetadataResult> ReadAsync(IRecroGridContext context, RecroGrid grid, int entityId,
            IReadOnlyCollection<RecroGrid>? relationTargets = null, CancellationToken cancellationToken = default)
            => ReadAsync(context, entityId, cancellationToken);
        public Task<RgfEntityMetadataResult> ReadAsync(IRecroGridContext context, int entityId, CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new RgfEntityMetadataResult(RgfMetadataReadStatus.Denied, null!, []));
        }
    }
}

internal static class MetadataJsonExtensions
{
    internal static int? GetInt32Nullable(this JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
}

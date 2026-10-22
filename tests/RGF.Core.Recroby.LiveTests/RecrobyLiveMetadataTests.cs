using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace RGF.Core.Recroby.LiveTests;

[Collection(LiveTestCollection.Name)]
public sealed class RecrobyLiveMetadataTests(LiveTestFixture fixture)
{
    public static bool LiveEnabled => LiveTestFixture.LiveEnabled;
    private const string CatalogTool = "rgf_recroby_metadata_catalog_search";
    private const string EntityTool = "rgf_recroby_metadata_entity_read";

    [Fact(SkipUnless = nameof(LiveEnabled), Skip = "Explicit helyi LLM-engedélyezést igényel.")]
    public async Task ReproducedTwoTurnConversationMustListAllEntities()
    {
        var first = await fixture.SendAsync("Milyen Entity-k vannak az alkalmazásban?");
        Assert.True(first.Success, Describe(first));
        Assert.False(string.IsNullOrWhiteSpace(first.ConversationId));
        Assert.False(string.IsNullOrWhiteSpace(first.ConversationToken));
        var second = await fixture.SendAsync("Sorold fel őket.", first);
        Assert.True(second.Success, Describe(second));
        Assert.Equal(first.ConversationId, second.ConversationId);
        var firstLog = await DiagnosticLog.ReadAsync(first);
        var secondLog = await DiagnosticLog.ReadAsync(second);
        var names = ExpectedNames(firstLog);
        // A friss olvasás és a már elérhető előzmény egyaránt érvényes metadataforrás.
        var previousCalls = ToolResults(firstLog, CatalogTool)
            .Where(tool => string.IsNullOrWhiteSpace(Get(tool.Arguments, "search").GetString()))
            .Select(tool => tool.CallId).ToArray();
        var historyAvailable = HistoryResultsAvailable(secondLog, previousCalls);
        var secondNames = historyAvailable ? names : ExpectedNames(secondLog);
        Assert.Multiple(() => AssertContainsNames(first.Message, names),
            () => AssertContainsNames(second.Message, secondNames));
    }

    [Fact(SkipUnless = nameof(LiveEnabled), Skip = "Explicit helyi LLM-engedélyezést igényel.")]
    public async Task ExplicitSingleTurnEnumerationMustContainNamesNotOnlyCount()
    {
        var response = await fixture.SendAsync("Sorold fel az alkalmazás összes elérhető Entity-jét a pontos EntityName értékükkel.");
        Assert.True(response.Success, Describe(response));
        AssertContainsNames(response.Message, ExpectedNames(await DiagnosticLog.ReadAsync(response)));
    }

    [Fact(SkipUnless = nameof(LiveEnabled), Skip = "Explicit helyi LLM-engedélyezést igényel.")]
    public async Task DetailedMetadataMustContainKnownPropertyNames()
    {
        var response = await fixture.SendAsync("Milyen Property-k találhatók a Customer Entity-ben? Sorold fel a pontos technikai neveket.");
        Assert.True(response.Success, Describe(response));
        var customer = Customer(await DiagnosticLog.ReadAsync(response));
        var names = Get(Get(customer.Result, "entity"), "properties").EnumerateArray()
            .Select(property => Get(property, "name").GetString()!).Distinct().ToArray();
        Assert.Contains("CustomerId", names);
        AssertContainsNames(response.Message, names);
    }

    [Fact(SkipUnless = nameof(LiveEnabled), Skip = "Explicit helyi LLM-engedélyezést igényel.")]
    public async Task PropertyFollowUpMustUseAuthorizedMetadataForTheKey()
    {
        var response = await fixture.SendAsync("Milyen Property-k találhatók a Customer Entity-ben? Sorold fel a pontos technikai neveket.");
        Assert.True(response.Success, Describe(response));
        var customer = Customer(await DiagnosticLog.ReadAsync(response));
        var key = Assert.Single(Get(Get(customer.Result, "entity"), "properties").EnumerateArray(),
            property => Get(property, "name").GetString() == "CustomerId");
        Assert.Contains("primary key", Get(key, "propertyComment").GetString()!, StringComparison.OrdinalIgnoreCase);
        var followUp = await fixture.SendAsync("És melyik Property az elsődleges kulcsa?", response);
        Assert.True(followUp.Success, Describe(followUp));
        Assert.Equal(response.ConversationId, followUp.ConversationId);
        AssertContainsNames(followUp.Message, ["CustomerId"]);
        var log = await DiagnosticLog.ReadAsync(followUp);
        if (!HistoryResultsAvailable(log, [customer.CallId]))
        {
            var fresh = Customer(log);
            Assert.Equal(Get(Get(customer.Result, "entity"), "entityId").GetInt32(),
                Get(Get(fresh.Result, "entity"), "entityId").GetInt32());
            var freshKey = Assert.Single(Get(Get(fresh.Result, "entity"), "properties").EnumerateArray(),
                property => Get(property, "name").GetString() == "CustomerId");
            Assert.Contains("primary key", Get(freshKey, "propertyComment").GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(DiagnosticLog.Traffic(log, "Request").SelectMany(DiagnosticLog.Contents), content =>
            Get(content, "Text").GetString()?.Contains("Milyen Property-k találhatók a Customer Entity-ben?", StringComparison.Ordinal) == true);
    }

    [Fact(SkipUnless = nameof(LiveEnabled), Skip = "Explicit helyi LLM-engedélyezést igényel.")]
    public async Task GeneralQuestionMustUseChatBranch()
    {
        var response = await fixture.SendAsync("Mennyi 7 meg 8? Válaszolj csak a számmal.");
        Assert.True(response.Success, Describe(response));
        AssertContainsNames(response.Message, ["15"]);
        var log = await DiagnosticLog.ReadAsync(response);
        Assert.Contains(log, row => row.String("AgentId") == "rgf.recroby.chat" && row.String("Status") == "Completed");
        Assert.DoesNotContain(log, row => row.String("AgentId") == "rgf.recroby.application");
    }

    private static string[] ExpectedNames(DiagnosticEvent[] log)
    {
        var tools = ToolResults(log, CatalogTool).Where(tool =>
            string.IsNullOrWhiteSpace(Get(tool.Arguments, "search").GetString())).ToArray();
        Assert.NotEmpty(tools);
        // Csak a szűretlen, nulláról induló, összefüggő teljes laplánc alkalmas referenciának.
        var pages = tools.GroupBy(tool => Get(tool.Arguments, "offset").GetInt32())
            .ToDictionary(group => group.Key, group => group.Last());
        var names = new List<string>();
        var ids = new HashSet<int>();
        var offset = 0;
        int? total = null;
        while (true)
        {
            Assert.True(pages.TryGetValue(offset, out var page), $"Hiányos katalógusolvasás: hiányzik az offset={offset} szűretlen lap; a modell nem olvasta végig a katalógust.");
            var result = page!.Result;
            var count = Get(result, "totalCount").GetInt32();
            Assert.True(count > 0, "Üres katalógus nem tekinthető teljes referenciának.");
            total ??= count;
            Assert.Equal(total.Value, count);
            var entities = Get(result, "entities").EnumerateArray().ToArray();
            Assert.NotEmpty(entities);
            Assert.True(entities.Length <= Get(page.Arguments, "pageSize").GetInt32());
            foreach (var entity in entities)
            {
                var id = Get(entity, "entityId").GetInt32();
                Assert.True(id > 10000 && ids.Add(id), "Érvénytelen vagy ismétlődő alkalmazás-Entity.");
                var name = Get(entity, "entityName").GetString();
                Assert.False(string.IsNullOrWhiteSpace(name));
                names.Add(name!);
            }
            var next = Get(result, "nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.Equal(offset + entities.Length, next.GetInt32());
            offset = next.GetInt32();
        }
        Assert.Equal(total!.Value, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        return names.ToArray();
    }

    private static ToolResult Customer(DiagnosticEvent[] log)
    {
        var reads = ToolResults(log, EntityTool);
        var customer = reads.LastOrDefault(tool =>
            Get(Get(tool.Result, "entity"), "name").GetString() is "RgCustomer" or "Customer");
        Assert.NotNull(customer);
        Assert.Equal(Get(customer.Arguments, "entityId").GetInt32(), Get(Get(customer.Result, "entity"), "entityId").GetInt32());
        var status = Get(customer.Result, "status");
        Assert.True(status.ValueKind == JsonValueKind.Number ? status.GetInt32() == 0 : status.GetString() == "Available",
            "A tényleges entity.read eredmény nem sikeres, jogosult metadata.");
        Assert.NotEmpty(Get(Get(customer.Result, "entity"), "properties").EnumerateArray());
        return customer;
    }

    private static ToolResult[] ToolResults(DiagnosticEvent[] log, string name)
    {
        var results = new List<ToolResult>();
        for (var index = 0; index < log.Length; index++)
        {
            var row = log[index];
            if (!DiagnosticLog.Traffic([row], "Response").Any()) continue;
            foreach (var call in DiagnosticLog.Contents(row).Where(content =>
                Get(content, "Kind").GetString() == "FunctionCall" && Get(content, "Name").GetString() == name))
            {
                var callId = Get(call, "CallId").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(callId));
                var request = DiagnosticLog.Traffic(log.Skip(index + 1), "Request").FirstOrDefault();
                Assert.NotNull(request);
                Assert.Contains(request.Traffic.GetProperty("Payload").GetProperty("Tools").EnumerateArray(),
                    tool => Get(tool, "Name").GetString() == name);
                var result = Assert.Single(DiagnosticLog.Contents(request), content =>
                    Get(content, "Kind").GetString() == "FunctionResult" && Get(content, "CallId").GetString() == callId);
                using var arguments = JsonDocument.Parse(Get(call, "Json").GetString()!);
                using var output = JsonDocument.Parse(Get(result, "Json").GetString()!);
                Assert.Equal(JsonValueKind.Object, output.RootElement.ValueKind);
                results.Add(new(callId, arguments.RootElement.Clone(), output.RootElement.Clone()));
            }
        }
        Assert.NotEmpty(results);
        return results.ToArray();
    }

    private static bool HistoryResultsAvailable(DiagnosticEvent[] log, IEnumerable<string> callIds)
    {
        var contents = DiagnosticLog.Traffic(log, "Request").SelectMany(DiagnosticLog.Contents).ToArray();
        return callIds.Distinct().All(id => contents.Any(content => Get(content, "Kind").GetString() == "FunctionResult"
                && Get(content, "CallId").GetString() == id && !string.IsNullOrWhiteSpace(Get(content, "Json").GetString())));
    }

    private static JsonElement Get(JsonElement value, string name) => value.EnumerateObject()
        .First(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void AssertContainsNames(string? answer, string[] names)
    {
        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.NotEmpty(names);
        var missing = names.Where(name => !Regex.IsMatch(answer!,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant)).ToArray();
        Assert.True(missing.Length == 0,
            $"Válaszminőségi hiba: {missing.Length}/{names.Length} technikai név hiányzik: {string.Join(", ", missing)}. Válasz: {answer}");
    }

    private static string Describe(LiveResponse response) => $"Recroby-hiba: {response.ErrorCode} – {response.Message}";
    private sealed record ToolResult(string CallId, JsonElement Arguments, JsonElement Result);
}

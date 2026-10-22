using Xunit;

namespace RGF.Core.Recroby.LiveTests;

[Collection(LiveTestCollection.Name)]
public sealed class RecrobyLiveSmokeTests(LiveTestFixture fixture)
{
    public static bool LiveEnabled => LiveTestFixture.LiveEnabled;

    [Fact(SkipUnless = nameof(LiveEnabled), Skip = "Explicit helyi LLM-engedélyezést és Duende környezetet igényel.")]
    public async Task AuthenticatedChatUsesConfiguredRealProvider()
    {
        var response = await fixture.SendAsync("Mennyi 7 meg 8? Válaszolj csak a számmal.");
        Assert.True(response.Success, $"Recroby-hiba: {response.ErrorCode}.");
        Assert.Equal("15", response.Message?.Trim());
        Assert.False(string.IsNullOrWhiteSpace(response.ConversationId));
        Assert.False(string.IsNullOrWhiteSpace(response.WorkflowRunId));
        var log = await DiagnosticLog.ReadAsync(response);
        var requests = DiagnosticLog.Traffic(log, "Request");
        var replies = DiagnosticLog.Traffic(log, "Response");
        Assert.NotEmpty(requests);
        foreach (var request in requests)
        {
            var invocation = request.String("InvocationId");
            Assert.False(string.IsNullOrWhiteSpace(invocation));
            var requestedModel = request.Traffic.GetProperty("Payload").GetProperty("ModelId").GetString();
            if (!string.IsNullOrWhiteSpace(requestedModel)) Assert.Equal(fixture.ExpectedModelId, requestedModel);
            var reply = Assert.Single(replies, row => row.String("InvocationId") == invocation);
            Assert.Equal(fixture.ExpectedModelId, reply.Traffic.GetProperty("Payload").GetProperty("ModelId").GetString());
            Assert.True(reply.Traffic.GetProperty("Payload").GetProperty("Usage").GetProperty("TotalTokens").GetInt64() > 0);
        }
        // A normalizált modellválasztás nem bizonyítja a fizikai HTTP-attempt tartalmát vagy számát.
        Assert.Contains(log, row => row.String("AgentId") == "rgf.recroby.chat" && row.String("Status") == "Completed");
    }
}

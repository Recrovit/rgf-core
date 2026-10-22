using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Xunit;

namespace RGF.Core.Recroby.LiveTests;

// RGF-DOC: rgf.legacy.rgf-ai.entity-conversation
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveTestCollection : ICollectionFixture<LiveTestFixture>
{
    public const string Name = "Authenticated live Recroby";
}

public sealed class LiveTestFixture : IAsyncLifetime
{
    // These public demo credentials are intended only for the local Duende test environment.
    private const string Username = "Admin";
    private const string Password = "rgfdemo";
    public static bool LiveEnabled => Environment.GetEnvironmentVariable("RGF_DEMO_LIVE_LLM") == "1";
    private LiveApplicationConfiguration? configuration;
    private LiveClientSettings? settings;
    private HttpClient? client;
    private UserAccessToken? token;
    private string logFilePath = "";
    internal string ExpectedModelId { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (!LiveEnabled) return;
#if !DEBUG
        throw new InvalidOperationException("LiveTests must be built in Debug to use the local RGF implementations.");
#endif
        configuration = LiveApplicationConfiguration.Load();
        settings = configuration.GetClientSettings();
        logFilePath = await DiagnosticLog.ResolveFilePathAsync(configuration.WebApiDirectory);
        var provider = configuration.WebApi["Recrovit:AI:DefaultProvider"];
        if (string.IsNullOrWhiteSpace(provider)) throw new InvalidOperationException("WebAPI has no default AI provider.");
        var providerPath = "Recrovit:AI:Providers:" + provider;
        var model = configuration.WebApi[providerPath + ":DefaultModel"];
        ExpectedModelId = configuration.WebApi[providerPath + ":Models:" + model]
            ?? throw new InvalidOperationException("WebAPI default AI model alias has no model mapping.");
        client = new HttpClient { BaseAddress = settings.ApiBaseAddress, Timeout = TimeSpan.FromMinutes(4) };
        await AuthenticateAsync();
        var catalog = await ReadCatalogAsync();
        if (catalog.GetProperty("defaultProvider").GetString() != provider
            || !catalog.GetProperty("providers").EnumerateArray().Any(item =>
                item.GetProperty("id").GetString() == provider && item.GetProperty("defaultModel").GetString() == model))
            throw new InvalidOperationException("The running backend provider/model defaults differ from current WebAPI configuration; restart the backend.");
        Console.WriteLine($"Live preflight: Debug/Development configuration, user login succeeded, provider={provider}, model={ExpectedModelId}, WebAPI secrets={configuration.WebApiSecretsId ?? "none"}, " +
            $"normalized={configuration.WebApi["Recrovit:AI:Diagnostics:Normalized:Enabled"]}, content={configuration.WebApi["Recrovit:AI:Diagnostics:Normalized:IncludeContent"]}, " +
            $"structured={configuration.WebApi["Recrovit:AI:Diagnostics:StructuredOutput:IncludeContent"]}, raw={configuration.WebApi["Recrovit:AI:Diagnostics:RawHttp:Enabled"]}.");
    }

    private async Task AuthenticateAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        token = await DuendeLogin.AuthenticateAsync(settings!, Username, Password, cancellation.Token);
        client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
    }

    internal async Task<JsonElement> ReadCatalogAsync()
    {
        if (client is null) throw new InvalidOperationException("Live fixture is not initialized.");
        try
        {
            using var response = await client.GetAsync("api/rgf/ai/recroby/catalog");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Authenticated Recroby catalog failed (HTTP {(int)response.StatusCode}); check RGF user access.");
            using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return catalog.RootElement.Clone();
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("The configured WebAPI could not be reached over trusted HTTPS; start the Debug/Development backend.");
        }
    }

    internal async Task<LiveResponse> SendAsync(string prompt, LiveResponse? previous = null)
    {
        if (client is null || configuration is null) throw new InvalidOperationException("Live fixture is not initialized.");
        if (token!.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(45)) await AuthenticateAsync();
        var logStart = DiagnosticLog.CapturePosition(logFilePath);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync("api/rgf/ai/recroby", new RgfAiRequest
            {
                CurrentUserMessage = prompt,
                ConversationId = previous?.ConversationId,
                ConversationToken = previous?.ConversationToken
            });
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("The configured WebAPI could not be reached over trusted HTTPS.");
        }
        using (response)
        {
            Assert.True(response.IsSuccessStatusCode, $"Recroby HTTP {(int)response.StatusCode}; check authenticated backend logs.");
            var result = await response.Content.ReadFromJsonAsync<RgfAiResponse>()
                ?? throw new InvalidOperationException("Recroby returned no response payload.");
            var snapshot = new LiveResponse(result, logStart, logFilePath);
            Console.WriteLine($"Workflow run={result.WorkflowRunId}, success={result.Success}, error={result.ErrorCode}");
            return snapshot;
        }
    }

    public ValueTask DisposeAsync()
    {
        client?.Dispose();
        configuration?.Dispose();
        token = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class LiveResponse(RgfAiResponse response, long logStart, string logFilePath)
{
    internal bool Success => response.Success;
    internal string? Message => response.Message;
    internal string? ConversationId => response.ConversationId;
    internal string? ConversationToken => response.ConversationToken;
    internal string? WorkflowRunId => response.WorkflowRunId;
    internal string? ErrorCode => response.ErrorCode;
    internal long LogStart { get; } = logStart;
    internal string LogFilePath { get; } = logFilePath;
}

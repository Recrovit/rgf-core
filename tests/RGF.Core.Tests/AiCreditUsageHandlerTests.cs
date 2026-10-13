using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Identity;
using Xunit;

namespace RGF.Core.Tests;

public sealed class AiCreditUsageHandlerTests
{
    [Theory]
    [InlineData(null, "provider-alias")]
    [InlineData("provider-alias/unknown", "provider-alias")]
    [InlineData("provider-alias/model-alias", null)]
    [InlineData("provider-alias/model-alias", "unknown")]
    [InlineData("unknown/model-alias", "provider-alias")]
    public async Task UnresolvedAliasNeverSettlesNativeModel(string? route, string? providerId)
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        var request = new AIProviderRequestContext();
        await Authorize(provider, request);
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<Recrovit.RecroGridFramework.AI.AiCreditConfigurationException>(async () =>
            await Handler(scope.ServiceProvider).OnUsageAsync(new("request", request, route, "actual-model",
                new(1, 1), AIProviderUsageStatus.Completed, providerId), Token));
        Assert.Empty(state.Usages);
    }

    [Fact]
    public async Task AliasesForSameNativeModelRemainDistinctInAccounting()
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        var request = new AIProviderRequestContext();
        await Authorize(provider, request);
        await using var scope = provider.CreateAsyncScope();
        foreach (var alias in new[] { "model-alias", "second-alias" })
            await Handler(scope.ServiceProvider).OnUsageAsync(new(alias, request, $"provider-alias/{alias}",
                "actual-model", new(1, 1), AIProviderUsageStatus.Completed, "provider-alias"), Token);
        Assert.Equal(new[] { "model-alias", "second-alias" }, state.Usages.Select(value => value.Usage.ModelId).Order());
    }

    [Theory]
    [InlineData(AIProviderUsageStatus.Completed)]
    [InlineData(AIProviderUsageStatus.Failed)]
    [InlineData(AIProviderUsageStatus.Cancelled)]
    [InlineData(AIProviderUsageStatus.Incomplete)]
    public async Task KnownUsagePreservesAllFieldsAndGuardIdentityAcrossScopes(AIProviderUsageStatus status)
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        var request = new AIProviderRequestContext(HostContext: new RgfRecrobyContext("attacker"));
        await Authorize(provider, request);
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).UserId = "another-user";
        await using var scope = provider.CreateAsyncScope();
        var usage = new AIProviderUsageContext("request", request, "provider-alias/model-alias", "actual-model",
            new(11, 12, 23, 4, 5), status, "provider-alias");
        await Handler(scope.ServiceProvider).OnUsageAsync(usage, Token);
        var recorded = Assert.Single(state.Usages);
        Assert.Equal("owner", recorded.UserId);
        Assert.Equal(new AiCreditUsage("request", "provider-alias", "model-alias", "provider-alias/model-alias", 11, 12, 23, 4, 5), recorded.Usage);
        Assert.Equal(Token, recorded.Token);
    }

    [Fact]
    public async Task MissingUsageSkipsAccountingEvenWithoutIdentity()
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        await using var scope = provider.CreateAsyncScope();
        await Handler(scope.ServiceProvider).OnUsageAsync(new("request", new(), null, null, null, AIProviderUsageStatus.Completed), Token);
        Assert.Empty(state.Usages);
    }

    [Fact]
    public async Task PartialUsageKeepsZerosUnknownValuesAndMissingMetadata()
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        var request = new AIProviderRequestContext();
        await Authorize(provider, request);
        await using var scope = provider.CreateAsyncScope();
        await Handler(scope.ServiceProvider).OnUsageAsync(new("request", request, "provider-alias/model-alias", null,
            new(0, null, null, 0, null), AIProviderUsageStatus.Incomplete, "provider-alias"), Token);
        Assert.Equal(new AiCreditUsage("request", "provider-alias", "model-alias", "provider-alias/model-alias", 0, null, null, 0, null), Assert.Single(state.Usages).Usage);
    }

    [Fact]
    public async Task IdentityUsesReferenceEqualityAndConflictingReassociationFails()
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        var request = new AIProviderRequestContext();
        await Authorize(provider, request);
        await Authorize(provider, request);
        await using var scope = provider.CreateAsyncScope();
        var usage = new AIProviderUsageContext("request", request, "provider-alias/model-alias", null, new(1), AIProviderUsageStatus.Completed, "provider-alias");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Handler(scope.ServiceProvider).OnUsageAsync(usage with { RequestContext = request with { } }, Token));
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).UserId = "other";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Authorize(provider, request));
        await Handler(scope.ServiceProvider).OnUsageAsync(usage, Token);
        Assert.Equal("owner", Assert.Single(state.Usages).UserId);
    }

    [Fact]
    public async Task DeniedContextDoesNotAcquireIdentity()
    {
        var state = new AiCreditTestState();
        state.Decisions.Enqueue(AiCreditDenialReason.UserDisabled);
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        var request = new AIProviderRequestContext();
        await Assert.ThrowsAsync<AIProviderRequestRejectedException>(() => Authorize(provider, request));
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Handler(scope.ServiceProvider)
            .OnUsageAsync(new("request", request, "provider-alias/model-alias", null, new(1), AIProviderUsageStatus.Completed, "provider-alias"), Token));
        Assert.Empty(state.Usages);
    }

    [Fact]
    public async Task ConcurrentAuthenticatedUsersRemainAssociatedWithTheirOwnContexts()
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).UsePrincipalUserId = true;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
        {
            AiCreditTestState.Authenticate(provider, $"user-{index}");
            var request = new AIProviderRequestContext();
            await Authorize(provider, request);
            if (Interlocked.Increment(ref count) == 20) ready.SetResult();
            await ready.Task;
            await using var scope = provider.CreateAsyncScope();
            await Handler(scope.ServiceProvider).OnUsageAsync(new($"request-{index}", request, "provider-alias/model-alias", null,
                new(index), AIProviderUsageStatus.Completed, "provider-alias"), Token);
        }));
        Assert.Equal(20, state.UsageCalls);
        Assert.All(state.Usages, usage => Assert.Equal($"user-{usage.Usage.InputTokens}", usage.UserId));
    }

    [Fact]
    public async Task RegistrationIsScopedAndIdempotentAndCreditErrorsPropagate()
    {
        var state = new AiCreditTestState { OnUsage = _ => throw new InvalidOperationException("credit failure") };
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        var request = new AIProviderRequestContext();
        await Authorize(provider, request);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        Assert.Same(Handler(first.ServiceProvider), Handler(first.ServiceProvider));
        Assert.NotSame(Handler(first.ServiceProvider), Handler(second.ServiceProvider));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Handler(first.ServiceProvider)
            .OnUsageAsync(new("request", request, "provider-alias/model-alias", null, new(1), AIProviderUsageStatus.Completed, "provider-alias"), Token));
        Assert.Equal(1, state.UsageCalls);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static IAIProviderUsageHandler Handler(IServiceProvider provider)
        => Assert.Single(provider.GetServices<IAIProviderUsageHandler>().OfType<RgfAiCreditUsageHandler>());
    private static async Task Authorize(IServiceProvider provider, AIProviderRequestContext request)
    {
        await using var scope = provider.CreateAsyncScope();
        await Assert.Single(scope.ServiceProvider.GetServices<IAIProviderRequestGuard>().OfType<RgfAiCreditRequestGuard>())
            .BeforeRequestAsync(request, Token);
    }
    private static ServiceProvider CreateProvider(AiCreditTestState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Recrovit.AI.Runtime.AiRouteCatalog(new Recrovit.AI.Runtime.Configuration.AiOptions
        {
            DefaultProvider = "provider-alias", Providers = new() { ["provider-alias"] = new()
                { DefaultModel = "model-alias", Models = new() { ["model-alias"] = "actual-model", ["second-alias"] = "actual-model" } } }
        }));
        state.Register(services);
        services.AddRgfRecroby();
        services.AddRgfRecroby();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ImplementationType == typeof(RgfAiCreditUsageHandler)).Lifetime);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

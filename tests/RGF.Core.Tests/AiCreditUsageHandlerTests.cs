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
        var usage = new AIProviderUsageContext("request", request, "route/not-a-provider", "actual-model",
            new(11, 12, 23, 4, 5), status, "provider-alias");
        await Handler(scope.ServiceProvider).OnUsageAsync(usage, Token);
        var recorded = Assert.Single(state.Usages);
        Assert.Equal("owner", recorded.UserId);
        Assert.Equal(new AiCreditUsage("request", "provider-alias", "actual-model", "route/not-a-provider", 11, 12, 23, 4, 5), recorded.Usage);
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
        await Handler(scope.ServiceProvider).OnUsageAsync(new("request", request, "provider/model", null,
            new(0, null, null, 0, null), AIProviderUsageStatus.Incomplete), Token);
        Assert.Equal(new AiCreditUsage("request", null, null, "provider/model", 0, null, null, 0, null), Assert.Single(state.Usages).Usage);
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
        var usage = new AIProviderUsageContext("request", request, null, null, new(1), AIProviderUsageStatus.Completed);
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
            .OnUsageAsync(new("request", request, null, null, new(1), AIProviderUsageStatus.Completed), Token));
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
            await Handler(scope.ServiceProvider).OnUsageAsync(new($"request-{index}", request, null, null,
                new(index), AIProviderUsageStatus.Completed), Token);
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
            .OnUsageAsync(new("request", request, null, null, new(1), AIProviderUsageStatus.Completed), Token));
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
        state.Register(services);
        services.AddRgfRecroby();
        services.AddRgfRecroby();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ImplementationType == typeof(RgfAiCreditUsageHandler)).Lifetime);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

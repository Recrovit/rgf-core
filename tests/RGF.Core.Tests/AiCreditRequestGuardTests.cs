using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Identity;
using Xunit;

namespace RGF.Core.Tests;

public sealed class AiCreditRequestGuardTests
{
    [Theory]
    [InlineData(AiCreditDenialReason.UserDisabled)]
    [InlineData(AiCreditDenialReason.InsufficientCredit)]
    [InlineData(AiCreditDenialReason.BalanceExpired)]
    [InlineData(AiCreditDenialReason.InvalidConfiguration)]
    [InlineData(AiCreditDenialReason.UserNotFound)]
    [InlineData(AiCreditDenialReason.InfrastructureFailure)]
    public async Task DenialReasonsHaveStableCodes(AiCreditDenialReason reason)
    {
        var state = new AiCreditTestState();
        state.Decisions.Enqueue(reason);
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        await using var scope = provider.CreateAsyncScope();
        var exception = await Assert.ThrowsAsync<AIProviderRequestRejectedException>(async () =>
            await Guard(scope.ServiceProvider).BeforeRequestAsync(new(HostContext: new RgfRecrobyContext("attacker")), Token));
        Assert.Equal($"AiCredit.{reason}", exception.ReasonCode);
        Assert.Equal("owner", Assert.Single(state.Users));
        Assert.Same(provider.GetRequiredService<IHttpContextAccessor>().HttpContext!.User,
            ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).Principal);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    [InlineData(true, "")]
    public async Task MissingIdentityDeniesBeforeCreditCheck(bool httpContext, string? userId)
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        if (httpContext) AiCreditTestState.Authenticate(provider);
        else provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).UserId = userId;
        await using var scope = provider.CreateAsyncScope();
        var exception = await Assert.ThrowsAsync<AIProviderRequestRejectedException>(async () =>
            await Guard(scope.ServiceProvider).BeforeRequestAsync(new(), Token));
        Assert.Equal("AiCredit.UserNotFound", exception.ReasonCode);
        Assert.Empty(state.Users);
    }

    [Fact]
    public async Task AnonymousIdentityDeniesBeforeIdentityLookup()
    {
        var state = new AiCreditTestState();
        await using var provider = CreateProvider(state);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<AIProviderRequestRejectedException>(async () =>
            await Guard(scope.ServiceProvider).BeforeRequestAsync(new(), Token));
        Assert.Null(((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).Principal);
        Assert.Empty(state.Users);
    }

    [Fact]
    public async Task InfrastructureAndCancellationPropagate()
    {
        var state = new AiCreditTestState { OnCheck = _ => throw new InvalidOperationException("database detail") };
        await using var provider = CreateProvider(state);
        AiCreditTestState.Authenticate(provider);
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Guard(scope.ServiceProvider).BeforeRequestAsync(new(), Token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Guard(scope.ServiceProvider).BeforeRequestAsync(new(), cancellation.Token));
        Assert.Single(state.Users);
    }

    [Fact]
    public async Task RegistrationIsIdempotentScopedAndPreservesOtherGuardsAndLateCreditOverride()
    {
        var state = new AiCreditTestState();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IAIProviderRequestGuard, OtherGuard>();
        services.AddRgfRecroby();
        services.AddRgfRecroby();
        state.Register(services);
        var descriptor = Assert.Single(services, d => d.ImplementationType == typeof(RgfAiCreditRequestGuard));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        AiCreditTestState.Authenticate(provider);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        Assert.Equal(2, first.ServiceProvider.GetServices<IAIProviderRequestGuard>().Count());
        Assert.Same(Guard(first.ServiceProvider), Guard(first.ServiceProvider));
        Assert.NotSame(Guard(first.ServiceProvider), Guard(second.ServiceProvider));
        await Guard(first.ServiceProvider).BeforeRequestAsync(new(), Token);
        Assert.IsType<TestCreditService>(first.ServiceProvider.GetRequiredService<IAiCreditService>());
    }

    [Fact]
    public async Task MissingCreditServiceFailsResolution()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(System.Reflection.DispatchProxy.Create<IRgfIdentityService, TestIdentityProxy>());
        services.AddRgfRecroby();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        Assert.Throws<InvalidOperationException>(() => Guard(scope.ServiceProvider));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static IAIProviderRequestGuard Guard(IServiceProvider provider)
        => provider.GetServices<IAIProviderRequestGuard>().Single(g => g is RgfAiCreditRequestGuard);
    private static ServiceProvider CreateProvider(AiCreditTestState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        state.Register(services);
        services.AddRgfRecroby();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    private sealed class OtherGuard : IAIProviderRequestGuard
    {
        public ValueTask BeforeRequestAsync(AIProviderRequestContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}

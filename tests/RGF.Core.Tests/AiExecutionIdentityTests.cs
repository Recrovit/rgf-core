using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Identity;
using Xunit;

namespace RGF.Core.Tests;

public sealed class AiExecutionIdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ServiceProvider Provider(AiCreditTestState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        state.Register(services);
        services.AddRgfRecroby();
        services.AddRgfRecroby();
        Assert.Single(services, d => d.ServiceType == typeof(IRgfAiExecutionIdentityFactory));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    private static async Task Check(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetServices<IAIProviderRequestGuard>().Single().BeforeRequestAsync(new(), Token);
    }
    private static async Task<IRgfAiExecutionIdentity> Capture(IServiceProvider provider, string user = "owner")
    {
        AiCreditTestState.Authenticate(provider, user);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRgfAiExecutionIdentityFactory>().CaptureAsync(Token);
    }
    private static async Task Denied(IServiceProvider provider)
    {
        var error = await Assert.ThrowsAsync<AIProviderRequestRejectedException>(() => Check(provider));
        Assert.Equal("AiCredit.UserNotFound", error.ReasonCode);
    }

    [Theory]
    [InlineData(false, "owner")]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    public async Task CaptureRequiresAuthenticatedNonemptyHttpIdentity(bool authenticated, string? user)
    {
        await using var provider = Provider(new());
        if (authenticated) AiCreditTestState.Authenticate(provider);
        else provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).UserId = user;
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.ServiceProvider
            .GetRequiredService<IRgfAiExecutionIdentityFactory>().CaptureAsync(Token));
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.ServiceProvider
            .GetRequiredService<IRgfAiExecutionIdentityFactory>().CaptureAsync(Token));
    }

    [Fact]
    public async Task BackgroundCrossScopeLateBranchesAndSuppressedFlowRespectLifetime()
    {
        var state = new AiCreditTestState();
        await using var provider = Provider(state);
        var identity = await Capture(provider);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? late = null;
        await identity.RunAsync(async _ =>
        {
            await Check(provider);
            late = Task.Run(async () => { await release.Task; await Denied(provider); }, Token);
            Task detached;
            using (ExecutionContext.SuppressFlow()) detached = Task.Run(() => Denied(provider), Token);
            await detached;
        }, Token);
        release.SetResult();
        await late!;
        await Denied(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.RunAsync(_ => Check(provider), Token));
        Assert.Equal("owner", Assert.Single(state.Users));
    }

    [Fact]
    public async Task ParallelUsersAreIsolatedAndNestedUserChangesAreRejected()
    {
        var state = new AiCreditTestState();
        await using var provider = Provider(state);
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).UsePrincipalUserId = true;
        var alice = await Capture(provider, "alice");
        var bob = await Capture(provider, "bob");
        var nested = await Capture(provider, "bob");
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task Work(CancellationToken _)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task;
            await Check(provider);
        }
        await Task.WhenAll(alice.RunAsync(async ct =>
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nested.RunAsync(_ => Check(provider), ct));
            await Work(ct);
        }, Token), bob.RunAsync(Work, Token));
        Assert.Equal(new[] { "alice", "bob" }, state.Users.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndCancellationCloseIdentity(bool cancelled)
    {
        await using var provider = Provider(new());
        var identity = await Capture(provider);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => identity.RunAsync(_ => throw new InvalidOperationException(), cancellation.Token));
        await Denied(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.RunAsync(_ => Check(provider), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpConflictOrAnonymousContextCannotBypassExecutionIdentity(bool anonymous)
    {
        var state = new AiCreditTestState();
        await using var provider = Provider(state);
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).UsePrincipalUserId = true;
        var identity = await Capture(provider);
        AiCreditTestState.Authenticate(provider, "other");
        if (anonymous) provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        await identity.RunAsync(_ => Denied(provider), Token);
        Assert.Empty(state.Users);
    }
    [Fact]
    public async Task SameUserNestedScopeRestoresParentAndOutlivingChildCannotExtendIt()
    {
        var state = new AiCreditTestState();
        await using var provider = Provider(state);
        var parent = await Capture(provider);
        var nested = await Capture(provider);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        await parent.RunAsync(async ct =>
        {
            await nested.RunAsync(_ => Check(provider), ct);
            await Check(provider);
        }, Token);
        await Denied(provider);
        Assert.Equal(new[] { "owner", "owner" }, state.Users);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionOrCancellationAfterAwaitInvalidatesInheritedBranches(bool cancelled)
    {
        await using var provider = Provider(new());
        var identity = await Capture(provider);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? late = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<Exception>(() => identity.RunAsync(async ct =>
        {
            late = Task.Run(async () => { await release.Task; await Denied(provider); }, Token);
            await Task.Yield();
            if (cancelled) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("operation failed");
        }, cancellation.Token));
        release.SetResult();
        await late!;
        await Denied(provider);
    }

    [Fact]
    public async Task CaptureRejectsHttpContextClearedDuringIdentityResolution()
    {
        await using var provider = Provider(new());
        AiCreditTestState.Authenticate(provider);
        var http = provider.GetRequiredService<IHttpContextAccessor>();
        ((TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>()).OnUserId = () => http.HttpContext = null;
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.ServiceProvider
            .GetRequiredService<IRgfAiExecutionIdentityFactory>().CaptureAsync(Token));
    }    [Fact]
    public async Task ScopeClosingDuringCreditCheckRejectsBeforeProviderAuthorization()
    {
        var state = new AiCreditTestState();
        await using var provider = Provider(state);
        var identity = await Capture(provider);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        state.OnCheck = _ =>
        {
            checking.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), Token));
        };
        Task? late = null;
        try
        {
            await identity.RunAsync(async _ =>
            {
                late = Task.Run(() => Denied(provider), Token);
                await checking.Task.WaitAsync(Token);
            }, Token);
        }
        finally { release.Set(); }
        await late!;
        Assert.Single(state.Users);
    }

    [Fact]
    public async Task NestedSameUserExecutionCannotOutliveItsParent()
    {
        await using var provider = Provider(new());
        var parent = await Capture(provider);
        var child = await Capture(provider);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? childTask = null;
        await parent.RunAsync(_ =>
        {
            childTask = child.RunAsync(async _ => { await release.Task; await Denied(provider); }, Token);
            return Task.CompletedTask;
        }, Token);
        release.SetResult();
        await childTask!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.RunAsync(_ => Check(provider), Token));
    }}

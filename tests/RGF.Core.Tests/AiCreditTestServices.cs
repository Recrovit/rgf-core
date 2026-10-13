using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Identity;

namespace RGF.Core.Tests;

internal sealed class AiCreditTestState
{
    public Queue<AiCreditDenialReason?> Decisions { get; } = new();
    public List<string> Users { get; } = [];
    public List<TestCreditService> Instances { get; } = [];
    public Action<CancellationToken>? OnCheck { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<(string UserId, AiCreditUsage Usage, CancellationToken Token)> Usages { get; } = new();
    public int UsageCalls => Usages.Count;
    public AiCreditMode Mode { get; set; } = AiCreditMode.Unlimited;
    public AiPrepaidBalance? Prepaid { get; set; }
    public IReadOnlyList<AiPeriodCredit> Periods { get; set; } = [];
    public decimal Charge { get; set; }
    public Func<AiCreditUsage, AiCreditUsageResult>? Settlement { get; set; }
    public Action<string>? OnState { get; set; }
    public Action<CancellationToken>? OnUsage { get; set; }

    public void Register(IServiceCollection services)
    {
        services.AddScoped<IAiCreditService>(_ => new TestCreditService(this));
        services.AddSingleton(DispatchProxy.Create<IRgfIdentityService, TestIdentityProxy>());
        services.AddHttpContextAccessor();
    }

    public static void Authenticate(IServiceProvider provider, string userId = "owner")
        => provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };
}

internal sealed class TestCreditService(AiCreditTestState state) : IAiCreditService, IAsyncDisposable
{
    public bool Disposed { get; private set; }
    public Task<AiCreditAccessResult> CheckAccessAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (state)
        {
            state.Users.Add(userId);
            state.Instances.Add(this);
            state.OnCheck?.Invoke(cancellationToken);
            return Task.FromResult(new AiCreditAccessResult(state.Decisions.Count == 0 ? null : state.Decisions.Dequeue()));
        }
    }
    public Task<AiCreditMode> GetCreditModeAsync(string userId, CancellationToken cancellationToken = default)
    {
        state.OnState?.Invoke(userId);
        return Task.FromResult(state.Mode);
    }
    public Task<AiPrepaidBalance?> GetPrepaidBalanceAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(state.Prepaid);
    public Task<IReadOnlyList<AiPeriodCredit>> GetPeriodCreditsAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(state.Periods);
    public Task<AiCreditUsageResult> ApplyUsageAsync(string userId, AiCreditUsage usage, CancellationToken cancellationToken = default)
    {
        state.Usages.Enqueue((userId, usage, cancellationToken));
        state.OnUsage?.Invoke(cancellationToken);
        return Task.FromResult(state.Settlement?.Invoke(usage) ?? new AiCreditUsageResult(usage.RequestId, state.Charge, true));
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

// DispatchProxy keeps unrelated identity operations outside the Credit fixture.
public class TestIdentityProxy : DispatchProxy
{
    public string? UserId { get; set; } = "owner";
    public string? UserName { get; set; } = "Test User";
    public bool FailCredentials { get; set; }
    public bool FailUserId { get; set; }
    public Action? OnUserId { get; set; }
    public bool UsePrincipalUserId { get; set; }
    public ClaimsPrincipal? Principal { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IRgfIdentityService.GetUserCredentials))
        {
            if (FailCredentials) throw new InvalidOperationException("private identity detail");
            Principal = (ClaimsPrincipal)args![0]!;
            return new RgfUserCredentials { UserId = UserId, UserName = UserName };
        }
        if (targetMethod?.Name != nameof(IRgfIdentityService.GetUserIdAsync)) throw new NotSupportedException();
        OnUserId?.Invoke();
        if (FailUserId) throw new InvalidOperationException("identity failure");
        Principal = (ClaimsPrincipal)args![0]!;
        return Task.FromResult(UsePrincipalUserId ? ((ClaimsPrincipal)args![0]!).FindFirstValue(ClaimTypes.NameIdentifier)! : UserId!);
    }
}

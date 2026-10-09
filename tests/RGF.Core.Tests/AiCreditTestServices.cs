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

    public void Register(IServiceCollection services)
    {
        services.AddScoped<IAiCreditService>(_ => new TestCreditService(this));
        services.AddSingleton(DispatchProxy.Create<IRgfIdentityService, TestIdentityProxy>());
        services.AddHttpContextAccessor();
    }

    public static void Authenticate(IServiceProvider provider)
        => provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        };
}

internal sealed class TestCreditService(AiCreditTestState state) : IAiCreditService, IAsyncDisposable
{
    public bool Disposed { get; private set; }
    public Task<AiCreditAccessResult> CheckAccessAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.Users.Add(userId);
        state.Instances.Add(this);
        state.OnCheck?.Invoke(cancellationToken);
        return Task.FromResult(new AiCreditAccessResult(state.Decisions.Count == 0 ? null : state.Decisions.Dequeue()));
    }
    public Task<AiCreditMode> GetCreditModeAsync(string userId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<AiPrepaidBalance?> GetPrepaidBalanceAsync(string userId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AiPeriodCredit>> GetPeriodCreditsAsync(string userId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

// DispatchProxy keeps unrelated identity operations outside the Credit fixture.
public class TestIdentityProxy : DispatchProxy
{
    public string? UserId { get; set; } = "owner";
    public string? UserName { get; set; } = "Test User";
    public bool FailCredentials { get; set; }
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
        Principal = (ClaimsPrincipal)args![0]!;
        return Task.FromResult(UserId!);
    }
}

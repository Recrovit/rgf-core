using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.Http;
using Recrovit.RecroGridFramework.Extensions;
using System.Text.Json;
using System.Reflection;
using Recrovit.RecroGridFramework.Security;
using Xunit;

namespace RGF.Core.Tests;

[CollectionDefinition("Core initialization", DisableParallelization = true)]
public sealed class CoreInitializationCollection { }

[Collection("Core initialization")]
public sealed class CoreInitializationTests : IDisposable
{
    private readonly Dictionary<FieldInfo, object?> state = typeof(RecroSec)
        .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        .Where(field => !field.IsInitOnly && !field.IsLiteral)
        .ToDictionary(field => field, field => field.GetValue(null));

    public CoreInitializationTests()
    {
        // Minden teszt egy új folyamat kezdeti állapotát modellezi.
        typeof(RecroSec).GetField("_singleUserMode", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        typeof(RecroSec).GetField("_allowSingleUserModeFromConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
    }

    public static IEnumerable<object?[]> SingleUserCases()
    {
        foreach (var webHost in new[] { false, true })
        {
            foreach (var allow in new bool?[] { null, false, true })
            {
                foreach (var configured in new bool?[] { null, false, true })
                {
                    yield return [webHost, allow, configured];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SingleUserCases))]
    public async Task SingleUserModeUsesLegacyConfiguration(bool webHost, bool? allow, bool? configured)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recrovit:RecroSec:SingleUserMode"] = configured?.ToString()
        });
        for (var registration = 0; registration < 2; registration++)
        {
            if (webHost)
            {
                if (allow.HasValue)
                {
                    builder.WebHost.AddRGF(allowSingleUserModeFromConfiguration: allow.Value);
                }
                else
                {
                    builder.WebHost.AddRGF();
                }
            }
            else if (allow.HasValue)
            {
                builder.AddRGF(allowSingleUserModeFromConfiguration: allow.Value);
            }
            else
            {
                builder.AddRGF();
            }
        }
        await using var app = builder.Build();
#if DEBUG
        const bool defaultAllow = true;
#else
        const bool defaultAllow = false;
#endif
        Assert.Equal((allow ?? defaultAllow) && configured == true, RecroSec.SingleUserMode);
        Assert.Equal(RecroSec.SingleUserMode, RecroSec.IsAdmin(string.Empty));
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(RgfCapabilitiesResponse));
    }

    public void Dispose()
    {
        foreach (var (field, value) in state)
        {
            field.SetValue(null, value);
        }
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, null)]
    [InlineData(true, true, true)]
    public async Task CoreOnlyExposesCapabilitiesWithoutRecroby(bool useWebHost, bool configuredEnabled, bool? allowFilterToLower)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recrovit:RecroGridFramework:Recroby:Enabled"] = configuredEnabled.ToString()
        });
        if (allowFilterToLower.HasValue)
            builder.Configuration["Recrovit:RecroGridFramework:AllowFilterToLower"] = allowFilterToLower.Value.ToString();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (useWebHost)
        {
            builder.WebHost.AddRGF();
            builder.WebHost.AddRGF();
        }
        else
        {
            builder.AddRGF();
            builder.AddRGF();
        }
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(RgfCapabilitiesResponse));
        Assert.DoesNotContain(typeof(RgfCapabilitiesController).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name!.StartsWith("Recrovit.AI") || assembly.Name.Contains("Recroby"));
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(app.Services.GetRequiredService<RgfCapabilitiesResponse>().RecrobyEnabled);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/rgf/capabilities", TestContext.Current.CancellationToken));
        Assert.False(json.RootElement.GetProperty("recrobyEnabled").GetBoolean());
        Assert.Equal(allowFilterToLower ?? true, json.RootElement.GetProperty("allowFilterToLower").GetBoolean());
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await client.GetAsync("/api/rgf/ai/recroby/catalog", TestContext.Current.CancellationToken)).StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}

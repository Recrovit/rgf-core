using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.Http;
using Recrovit.RecroGridFramework.Extensions;
using Xunit;

namespace RGF.Core.Tests;

public sealed class CoreInitializationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CoreOnlyExposesCapabilitiesWithoutRecroby(bool useWebHost, bool configuredEnabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recrovit:RecroGridFramework:Recroby:Enabled"] = configuredEnabled.ToString()
        });
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
        Assert.Contains("false", await client.GetStringAsync("/api/rgf/capabilities", TestContext.Current.CancellationToken));
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await client.GetAsync("/api/rgf/ai/recroby/catalog", TestContext.Current.CancellationToken)).StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}

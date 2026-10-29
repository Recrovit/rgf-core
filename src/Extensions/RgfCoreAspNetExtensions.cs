using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.Http;

namespace Recrovit.RecroGridFramework.Extensions;

// RGF-DOC: rgf.core.initialization
/// <summary>Initializes the base RGF services and Core features together.</summary>
public static class RgfCoreAspNetExtensions
{
    /// <summary>Initializes base RGF and Core features using the application configuration.</summary>
    /// <param name="builder">The application builder receiving the registrations.</param>
    /// <param name="singleUserMode">The base backend single-user setting; defaults to true.</param>
    /// <returns>The supplied builder.</returns>
    /// <remarks>Core features are registered once per service collection; the host owns the HTTP pipeline.</remarks>
    public static WebApplicationBuilder AddRGF(this WebApplicationBuilder builder, bool singleUserMode = true)
    {
        RgfBackendFeatureExtensions.AddRGF(builder, singleUserMode, new CoreFeature());
        return builder;
    }

    /// <summary>Schedules base RGF and Core feature initialization for the web host.</summary>
    /// <param name="builder">The web host builder receiving the service callback.</param>
    /// <param name="singleUserMode">The base backend single-user setting; defaults to true.</param>
    /// <returns>The supplied builder.</returns>
    /// <remarks>Uses host configuration and the same enablement rules as the application-builder overload.</remarks>
    public static IWebHostBuilder AddRGF(this IWebHostBuilder builder, bool singleUserMode = true)
        => RgfBackendFeatureExtensions.AddRGF(builder, singleUserMode, new CoreFeature());

    private sealed class CoreFeature : IRgfBackendFeature
    {
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(CoreFeature))) return;
            services.AddSingleton(this);
            services.TryAddSingleton(new RgfCapabilitiesResponse(false));
            services.AddControllers().AddApplicationPart(typeof(RgfCapabilitiesController).Assembly);
        }
    }
}

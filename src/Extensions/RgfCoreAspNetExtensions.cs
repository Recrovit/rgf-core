using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.Http;
using Recrovit.AI.Runtime;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Http;

namespace Recrovit.RecroGridFramework.Extensions;

// RGF-DOC: rgf.core.initialization
/// <summary>Initializes the base RGF services and Core features together.</summary>
public static class RgfCoreAspNetExtensions
{
    /// <summary>Initializes base RGF and Core features using the application configuration.</summary>
    /// <param name="builder">The application builder receiving the registrations.</param>
    /// <param name="singleUserMode">The base backend single-user setting; defaults to true.</param>
    /// <returns>The supplied builder.</returns>
    /// <remarks>Recroby is enabled by default. The Recroby:Enabled setting under
    /// Recrovit:RecroGridFramework disables its automatic services and controller discovery.
    /// Core features are registered once per service collection; the host owns the HTTP pipeline.</remarks>
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
            var enabled = configuration.GetValue("Recrovit:RecroGridFramework:Recroby:Enabled", true);
            services.AddSingleton(new RgfCapabilitiesResponse(enabled));
            services.AddControllers().AddApplicationPart(typeof(RgfCapabilitiesController).Assembly)
                .ConfigureApplicationPartManager(manager =>
                {
                    if (!enabled) manager.FeatureProviders.Add(new DisabledRecrobyController());
                });
            if (!enabled) return;
            services.AddRecrovitAI(configuration, _ => { });
            services.AddRgfRecroby();
            services.AddRgfRecrobyHttpApi();
        }
    }

    private sealed class DisabledRecrobyController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var controller = feature.Controllers.FirstOrDefault(type => type.AsType() == typeof(RgfRecrobyController));
            if (controller is not null) feature.Controllers.Remove(controller);
        }
    }

}

using Microsoft.Extensions.DependencyInjection;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Http;

// RGF-DOC: rgf.core.initialization
/// <summary>Adds the Core HTTP application part to MVC controller discovery.</summary>
public static class RgfRecrobyHttpServiceCollectionExtensions
{
    /// <summary>Registers the Recroby HTTP controller for custom composition; standard AddRGF initialization calls this automatically.</summary>
    /// <param name="services">The service collection receiving MVC registrations.</param>
    /// <returns>The MVC builder for further controller configuration.</returns>
    /// <remarks>Does not register the Recroby facade or configure authentication, authorization or endpoint mapping.</remarks>
    public static IMvcBuilder AddRgfRecrobyHttpApi(this IServiceCollection services)
        => services.AddControllers().AddApplicationPart(typeof(RgfRecrobyController).Assembly);
}

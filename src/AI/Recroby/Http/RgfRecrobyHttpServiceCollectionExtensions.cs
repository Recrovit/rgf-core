using Microsoft.Extensions.DependencyInjection;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Http;

public static class RgfRecrobyHttpServiceCollectionExtensions
{
    /// <summary>Adds the Recroby HTTP controller. Register Recroby and the host's RGF identity services separately.</summary>
    public static IMvcBuilder AddRgfRecrobyHttpApi(this IServiceCollection services)
        => services.AddControllers().AddApplicationPart(typeof(RgfRecrobyController).Assembly);
}

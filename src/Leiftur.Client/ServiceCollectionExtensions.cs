using Leiftur.Client;

namespace Microsoft.Extensions.DependencyInjection;

public static class LeifturClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILeifturClient"/>.
    /// <code>
    /// builder.Services.AddLeifturClient(o =>
    /// {
    ///     o.BaseAddress = new Uri("https://api.leiftur.is");
    ///     o.ApiKey = builder.Configuration["Leiftur:ApiKey"]!;
    /// });
    /// </code>
    /// </summary>
    public static IHttpClientBuilder AddLeifturClient(this IServiceCollection services, Action<LeifturClientOptions> configure)
    {
        services.Configure(configure);
        return services.AddHttpClient<ILeifturClient, LeifturClient>();
    }
}

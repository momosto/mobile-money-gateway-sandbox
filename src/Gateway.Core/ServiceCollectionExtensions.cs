using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayCore(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        services.AddSingleton(options);
        services.AddDbContext<GatewayDbContext>(o => o.UseNpgsql(config.GetConnectionString("Gateway")));
        services.AddHttpClient<IProviderClient, HttpProviderClient>(c =>
        {
            c.BaseAddress = new Uri(config["Provider:BaseUrl"] ?? "http://localhost:5090");
            c.Timeout = TimeSpan.FromSeconds(double.Parse(config["Provider:TimeoutSeconds"] ?? "3"));
        });
        services.AddScoped<PaymentService>();
        services.AddScoped<ReconciliationService>();
        return services;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThermixStudio.Core;

namespace ThermixStudio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddThermixInfrastructure(this IServiceCollection services, string dbPath)
    {
        services.AddDbContext<ThermixDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<IAppDataService, AppDataService>();
        return services;
    }
}
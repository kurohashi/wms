using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wms.PickList.Application.Common;
using Wms.PickList.Application.Services;
using Wms.PickList.Application.Services.Cancellation;

namespace Wms.PickList.Application;

public static class PickListApplicationDependencyInjection
{
    public static IServiceCollection AddPickListCancellation(this IServiceCollection services)
    {
        services.AddScoped<PendingOrAssigned>();
        services.AddScoped<InProgress>();
        services.AddScoped<Completed>();
        services.AddScoped<IPickListCancellationService, PickListCancellationService>();
        services.AddScoped<SyncInventory>();

        services.AddSingleton<PickListCancellationQueue>();
        services.AddSingleton<IPickListCancellationQueue>(sp =>
            sp.GetRequiredService<PickListCancellationQueue>());
        services.AddHostedService<PickListCancellationBackgroundService>();

        return services;
    }
    public static IServiceCollection AddPickListBackgroundGeneration(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<PickListSettings>(configuration.GetSection(PickListSettings.SectionName));
        }
        else
        {
            services.AddOptions<PickListSettings>()
                .Configure<IConfiguration>((settings, config) =>
                {
                    config.GetSection(PickListSettings.SectionName).Bind(settings);
                });
        }

        services.AddScoped<OrderInventoryAllocationService>();
        services.AddScoped<PickListGenerationService>();

        services.AddSingleton<GeneratePickListsQueue>();
        services.AddSingleton<IGeneratePickListsQueue>(sp =>
            sp.GetRequiredService<GeneratePickListsQueue>());
        services.AddHostedService<GeneratePickListsBackgroundService>();

        services.AddSingleton<GenerateShortPickListsQueue>();
        services.AddSingleton<IGenerateShortPickListsQueue>(sp =>
            sp.GetRequiredService<GenerateShortPickListsQueue>());
        services.AddHostedService<GenerateShortPickListsBackgroundService>();

        services.AddSingleton<GenerateSortItemsPickListsQueue>();
        services.AddSingleton<IGenerateSortItemsPickListsQueue>(sp =>
            sp.GetRequiredService<GenerateSortItemsPickListsQueue>());
        services.AddHostedService<GenerateSortItemsPickListsBackgroundService>();

        return services;
    }
}

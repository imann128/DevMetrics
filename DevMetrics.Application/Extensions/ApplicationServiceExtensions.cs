using System.Threading.Channels;
using DevMetrics.Application.BackgroundServices;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Services;
using DevMetrics.Application.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevMetrics.Application.Extensions;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        ArgumentNullException.ThrowIfNull(services,      nameof(services));
        ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));

        // MediatR
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(
                typeof(ApplicationServiceExtensions).Assembly));

        // Progress channel
        services.AddSingleton(Channel.CreateBounded<ScanProgressEvent>(
            new BoundedChannelOptions(capacity: 256)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = false
            }));

        // IScanNotifier (default no-op — overridden by Api layer)
        services.AddScoped<IScanNotifier, NullScanNotifier>();

        // Email
        services.AddScoped<IEmailService, EmailService>();

        // Configuration binding
        services.Configure<EmailSettings>(
            configuration.GetSection(EmailSettings.SectionName));

        services.Configure<CronSettings>(
            configuration.GetSection(CronSettings.SectionName));

        // EmailSettings startup validation
        services.AddSingleton<IValidateOptions<EmailSettings>, EmailSettingsValidator>();

        // Background services
        services.AddHostedService<ScanBackgroundService>();
        services.AddHostedService<WeeklyReportBackgroundService>();
        services.AddHostedService<RepositoryWatcherBackgroundService>();

        return services;
    }
}

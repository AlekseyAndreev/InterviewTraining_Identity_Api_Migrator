using InterviewTraining_Identity_Api_Migrator.Models;
using InterviewTraining_Identity_Api_Migrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;
using Elastic.Serilog.Sinks;

namespace InterviewTraining_Identity_Api_Migrator;

///<summary>
/// Мигратор данных между IdentityServer и InterviewTraining API
///</summary>
class Program
{
    static async Task Main(string[] args)
    {
        // Настройка конфигурации
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Настройка Serilog
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", "InterviewTraining_Identity_Api_Migrator");

        // Sink для консоли в JSON формате
        loggerConfig.WriteTo.Console(new CompactJsonFormatter());

        // Sink для Elasticsearch
        loggerConfig.WriteTo.Elasticsearch();

        Log.Logger = loggerConfig.ReadFrom.Configuration(configuration).CreateLogger();

        // Настройка DI контейнера с Serilog
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging(builder =>
            {
                builder.AddSerilog(Log.Logger);
            })
            .AddSingleton<IMigrationService, MigrationService>()
            .BuildServiceProvider();

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var migrationService = serviceProvider.GetRequiredService<IMigrationService>();
        var settings = configuration.GetSection("MigrationSettings").Get<MigrationSettings>() ?? new MigrationSettings();

        var interval = TimeSpan.FromMinutes(settings.IntervalMinutes);
        logger.LogInformation("Мигратор запущен. Интервал: {Interval} минут", 
            settings.IntervalMinutes);

        using var cts = new CancellationTokenSource();
        // Обработка завершения приложения
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Получен сигнал завершения. Остановка мигратора...");
            cts.Cancel();
        };

        // Бесконечный цикл с глобальным try-catch
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Запуск цикла миграции в {Time}", DateTime.UtcNow);
                await migrationService.RunMigrationCycleAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при выполнении цикла миграции");
            }

            try
            {
                logger.LogInformation("Ожидание {Interval} минут до следующего цикла", settings.IntervalMinutes);
                await Task.Delay(interval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Мигратор остановлен");
                break;
            }
        }

        logger.LogInformation("Мигратор завершен");
        Log.CloseAndFlush();
    }
}

using Microsoft.Extensions.Configuration;

namespace ServiceDeskCallSimulator.Configuration;

public static class SimulatorConfiguration
{
    public static IConfigurationRoot BuildConfiguration(
        string basePath,
        string? environmentVariablePrefix = SimulatorSettings.ConfigurationPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(basePath, "appsettings.json"), optional: false, reloadOnChange: false);

        if (!string.IsNullOrWhiteSpace(environmentVariablePrefix))
        {
            builder.AddEnvironmentVariables(environmentVariablePrefix);
        }

        return builder.Build();
    }

    public static SimulatorSettings LoadSettings(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = configuration.Get<SimulatorSettings>()
            ?? throw new InvalidOperationException("Unable to bind simulator settings from configuration.");

        SimulatorSettingsValidator.ValidateAndThrow(settings);
        return settings;
    }

    public static SimulatorSettings LoadSettingsFrom(
        string basePath,
        string? environmentVariablePrefix = SimulatorSettings.ConfigurationPrefix)
    {
        return LoadSettings(BuildConfiguration(basePath, environmentVariablePrefix));
    }
}

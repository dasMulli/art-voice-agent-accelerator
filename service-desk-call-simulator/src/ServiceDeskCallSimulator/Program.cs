using Microsoft.Extensions.DependencyInjection;
using ServiceDeskCallSimulator.Azure;
using ServiceDeskCallSimulator.Configuration;
using ServiceDeskCallSimulator.UI;

namespace ServiceDeskCallSimulator;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        // Configuration and DI composition are synchronous, local-only operations (reading
        // appsettings.json and registering factories). No Azure/ACS/Kestrel/Dev Tunnel calls
        // happen until MainForm.OnShown starts initialization, so the window shows immediately.
        var settings = SimulatorConfiguration.LoadSettingsFrom(AppContext.BaseDirectory);
        using var services = new ServiceCollection()
            .AddServiceDeskCallSimulatorCore(settings)
            .BuildServiceProvider();

        using var mainForm = new MainForm(settings, services);
        Application.Run(mainForm);
    }
}
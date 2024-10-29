using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace WindowsServicesViewer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Configure Serilog for logging to console and file
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug() // Set minimum logging level
                .WriteTo.Console()
                .WriteTo.File("logs/ServiceController-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            try
            {
                Log.Information("===========================");
                Log.Information("Starting Service Manager...");
                Log.Information("===========================");

                // Load appsettings.json
                IConfiguration config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                // Bind the "Services" section
                var servicesToManage = config.GetSection("Services").Get<List<ServiceConfig>>() ?? new List<ServiceConfig>();

                // Bind the "RestartServices" section
                var restartServices = config.GetSection("RestartServices").Get<List<RestartServiceConfig>>() ?? new List<RestartServiceConfig>();

                // Manage the stop and start of services
                foreach (var serviceConfig in servicesToManage)
                {
                    await ManageService(serviceConfig);
                }

                // Manage the restart of services
                foreach (var restartServiceConfig in restartServices)
                {
                    await RestartService(restartServiceConfig);
                }
                Log.Information("==========================================");
                Log.Information("All scheduled services have been managed.");
                Log.Information("==========================================");
            }
            catch (Exception ex)
            {   //Error
                Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
                Log.Error(ex, "An error occurred during service management.");
                Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
            }
            finally
            {
                Log.CloseAndFlush();
                Console.ReadKey();
            }
        }

        private static async Task ManageService(ServiceConfig serviceConfig)
        {
            try
            {
                // Find the services by name
                ServiceController service = ServiceController.GetServices()
                    .FirstOrDefault(s => s.ServiceName.Equals(serviceConfig.Name, StringComparison.OrdinalIgnoreCase));

                if (service == null)
                {
                    Log.Information("++++++++++++++++++++++++++++++++++++++");
                    Log.Warning($"Service '{serviceConfig.Name}' not found.");
                    Log.Information("++++++++++++++++++++++++++++++++++++++");

                    return;
                }

                // Parse stop and start times
                if (!DateTime.TryParse(serviceConfig.StopTime, out DateTime stopTime) ||
                    !DateTime.TryParse(serviceConfig.StartTime, out DateTime startTime))
                {
                    Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
                    Log.Warning($"Invalid time format for service '{serviceConfig.Name}'. Skipping...");
                    Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");

                    return;
                }

                // Combine current date with stop and start times
                DateTime currentDate = DateTime.Now;
                DateTime scheduledStopTime = new DateTime(currentDate.Year, currentDate.Month, currentDate.Day, stopTime.Hour, stopTime.Minute, 0);
                DateTime scheduledStartTime = new DateTime(currentDate.Year, currentDate.Month, currentDate.Day, startTime.Hour, startTime.Minute, 0);

                // Check time order
                if (scheduledStopTime >= scheduledStartTime)
                {
                    Log.Information("***************************************************************************");
                    Log.Warning($"Stop time must be earlier than start time for service '{serviceConfig.Name}'. Skipping...");
                    Log.Information("***************************************************************************");
                    return;
                }
                Log.Information("===========================================================================");
                Log.Information($"Scheduled stop time for {serviceConfig.Name}: {scheduledStopTime:hh:mm tt}");
                Log.Information("---------------------------------------------------------------------------");
                Log.Information($"Scheduled start time for {serviceConfig.Name}: {scheduledStartTime:hh:mm tt}");
                Log.Information("===========================================================================");

                // Calculate delays
                TimeSpan stopDelay = scheduledStopTime - DateTime.Now;
                TimeSpan startDelay = scheduledStartTime - DateTime.Now;

                // Wait until stop time
                if (stopDelay.TotalSeconds > 0)
                {
                    Log.Information("---------------------------------------------------------------------------");
                    Log.Information($"Waiting to stop service '{serviceConfig.Name}' at {scheduledStopTime:hh:mm tt}...");
                    Log.Information("---------------------------------------------------------------------------");
                    await Task.Delay(stopDelay);
                }

                // Stop the service
                if (service.Status == ServiceControllerStatus.Running)
                {
                    Log.Information("===========================================================================");
                    Log.Information($"Stopping service: {service.DisplayName}...");
                    Log.Information("---------------------------------------------------------------------------");
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    Log.Information($"Service '{service.DisplayName}' has been stopped successfully.");
                    Log.Information("===========================================================================");
                }

                // Wait until start time
                if (startDelay.TotalSeconds > 0)
                {
                    Log.Information("---------------------------------------------------------------------------");
                    Log.Information($"Waiting to start service '{serviceConfig.Name}' at {scheduledStartTime:hh:mm tt}...");
                    Log.Information("---------------------------------------------------------------------------");

                    await Task.Delay(startDelay - stopDelay);
                }

                // Start the service
                Log.Information("===========================================================================");
                Log.Information($"Starting service: {service.DisplayName}...");
                Log.Information("---------------------------------------------------------------------------");

                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Log.Information($"Service '{service.DisplayName}' has been started successfully.");
                Log.Information("===========================================================================");
            }
            catch (Exception ex)
            {
                Log.Information("***************************************************************************");
                Log.Error(ex, $"An error occurred while managing service '{serviceConfig.Name}': {ex.Message}");
                Log.Information("***************************************************************************");
            }
        }

        private static async Task RestartService(RestartServiceConfig restartServiceConfig)
        {
            try
            {
                // Find the service by name
                ServiceController service = ServiceController.GetServices()
                    .FirstOrDefault(s => s.ServiceName.Equals(restartServiceConfig.Name, StringComparison.OrdinalIgnoreCase));

                if (service == null)
                {
                    Log.Information("***************************************************************************");
                    Log.Warning($"Restart service '{restartServiceConfig.Name}' not found.");
                    Log.Information("***************************************************************************");
                    return;
                }

                // Parse restart time
                if (!DateTime.TryParse(restartServiceConfig.RestartTime, out DateTime restartTime))
                {
                    Log.Information("**************************************************************************************");
                    Log.Warning($"Invalid time format for restart service '{restartServiceConfig.Name}'. Skipping...");
                    Log.Information("**************************************************************************************");
                    return;
                }

                // Combine current date with restart time
                DateTime currentDate = DateTime.Now;
                DateTime scheduledRestartTime = new DateTime(currentDate.Year, currentDate.Month, currentDate.Day, restartTime.Hour, restartTime.Minute, 0);

                // Calculate delay
                TimeSpan restartDelay = scheduledRestartTime - DateTime.Now;

                // Wait until restart time
                if (restartDelay.TotalSeconds > 0)
                {
                    Log.Information("===========================================================================");
                    Log.Information($"Waiting to restart service '{restartServiceConfig.Name}' at {scheduledRestartTime:hh:mm tt}...");
                    Log.Information("===========================================================================");
                    await Task.Delay(restartDelay);
                }

                // Restart the service
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    Log.Information("===========================================================================");
                    Log.Information($"Stopping service: {service.DisplayName}...");
                    Log.Information("---------------------------------------------------------------------------");

                    service.Stop();

                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    Log.Information($"Service '{service.DisplayName}' has been stopped successfully.");
                    Log.Information("===========================================================================");
                }

                Log.Information("===========================================================================");
                Log.Information($"Starting service: {service.DisplayName}...");
                Log.Information("---------------------------------------------------------------------------");
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Log.Information($"Service '{service.DisplayName}' has been restarted successfully.");
                Log.Information("===========================================================================");

            }
            catch (Exception ex)
            {
                Log.Information("**************************************************************************************");
                Log.Error(ex, $"An error occurred while restarting service '{restartServiceConfig.Name}': {ex.Message}");
                Log.Information("**************************************************************************************");
            }
        }
    }

    // Class to represent service configuration from appsettings.json
    public class ServiceConfig
    {
        public string? Name { get; set; }
        public string? StopTime { get; set; }
        public string? StartTime { get; set; }
    }

    // Class to represent restart service configuration from appsettings.json
    public class RestartServiceConfig
    {
        public string? Name { get; set; }
        public string? RestartTime { get; set; }
    }
}

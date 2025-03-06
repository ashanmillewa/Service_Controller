using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ServiceProcess;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace WindowsServicesViewer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Load appsettings.json
            IConfiguration config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // Get log path from configuration and check for null
            string logPath = config.GetValue<string>("Logging:LogPath") ?? "logs/ServiceController-.txt";

            // Configure Serilog for logging to console and file
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            try
            {
                Log.Information("===========================");
                Log.Information("Starting Service Manager...");
                Log.Information("===========================");

                // Bind the "Services" section and check if null
                var servicesToManage = config.GetSection("Services").Get<List<ServiceConfig>>() ?? new List<ServiceConfig>();

                // Bind the "RestartServices" section and check if null
                var restartServices = config.GetSection("RestartServices").Get<List<RestartServiceConfig>>() ?? new List<RestartServiceConfig>();

                // Manage the stop and start of services
                foreach (var serviceConfig in servicesToManage)
                {
                    if (serviceConfig != null)
                    {
                        await ManageService(serviceConfig);
                    }
                    else
                    {
                        Log.Information("===============================================================");
                        Log.Warning("A null service configuration entry was found in Services list.");
                        Log.Information("===============================================================");
                    }
                }

                // Manage the restart of services
                foreach (var restartServiceConfig in restartServices)
                {
                    if (restartServiceConfig != null)
                    {
                        await RestartService(restartServiceConfig);
                    }
                    else
                    {
                        Log.Information("===========================================================================");
                        Log.Warning("A null restart service configuration entry was found in RestartServices list.");
                        Log.Information("===========================================================================");
                    }
                }

                Log.Information("==========================================");
                Log.Information("All scheduled services have been managed.");
                Log.Information("==========================================");
            }
            catch (Exception ex)
            {
                Log.Information("*********************************************");
                Log.Error(ex, "An error occurred during service management.");
                Log.Information("*********************************************");
            }
            finally
            {
                Log.CloseAndFlush();
                Console.ReadKey();
            }
        }

        private static async Task ManageService(ServiceConfig serviceConfig)
        {
            if (string.IsNullOrWhiteSpace(serviceConfig.Name))
            {
                Log.Information("**********************************************************************");
                Log.Warning("Service name is missing or empty. Skipping this service configuration.");
                Log.Information("**********************************************************************");
                return;
            }

            try
            {
                // Find the service by name
                ServiceController service = ServiceController.GetServices()
                    .FirstOrDefault(s => s.ServiceName.Equals(serviceConfig.Name, StringComparison.OrdinalIgnoreCase));

                if (service == null)
                {
                    Log.Information("***************************************************************");
                    Log.Warning($"Service '{serviceConfig.Name}' is not available.");
                    Log.Information("***************************************************************");
                    return;
                }

                // Parse stop and start times, with null checking
                if (!DateTime.TryParse(serviceConfig.StopTime, out DateTime stopTime) ||
                    !DateTime.TryParse(serviceConfig.StartTime, out DateTime startTime))
                {
                    Log.Information("******************************************************************************");
                    Log.Warning($"Invalid or missing stop/start time for service '{serviceConfig.Name}'. Skipping...");
                    Log.Information("******************************************************************************");
                    return;
                }

                DateTime currentDate = DateTime.Now;
                DateTime scheduledStopTime = new DateTime(currentDate.Year, currentDate.Month, currentDate.Day, stopTime.Hour, stopTime.Minute, 0);
                DateTime scheduledStartTime = new DateTime(currentDate.Year, currentDate.Month, currentDate.Day, startTime.Hour, startTime.Minute, 0);

                if (scheduledStopTime >= scheduledStartTime)
                {
                    Log.Information("===========================================================================");
                    Log.Warning($"Stop time must be earlier than start time for service '{serviceConfig.Name}'. Skipping...");
                    Log.Information("===========================================================================");
                    return;
                }

                if (scheduledStopTime < currentDate || scheduledStartTime < currentDate)
                {
                    Log.Information("==========================================================");
                    Log.Information($"Today's scheduled time for '{serviceConfig.Name}' has passed.");
                    Log.Information($"Service '{serviceConfig.Name}' will start/stop tomorrow.");
                    Log.Information("==========================================================");
                    return;
                }

                // Wait until scheduled stop time
                TimeSpan stopDelay = scheduledStopTime - DateTime.Now;
                if (stopDelay.TotalSeconds > 0)
                {
                    Log.Information("===========================================================================");
                    Log.Information($"Waiting to stop service '{serviceConfig.Name}' at {scheduledStopTime:hh:mm tt}...");
                    await Task.Delay(stopDelay);
                }

                try
                {
                    // Attempt to stop the service.
                    if (service.Status == ServiceControllerStatus.Running)
                    {
                        Log.Information($"Stopping service: {service.DisplayName}...");
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        Log.Information($"Service '{service.DisplayName}' has been stopped successfully.");
                    }
                }
                catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 5)
                {
                    Log.Warning($"Access denied while trying to manage service '{serviceConfig.Name}'. Make sure the application is running with administrative privileges.");
                }


                // Recalculate remaining delay until start time
                TimeSpan remainingDelay = scheduledStartTime - DateTime.Now;
                if (remainingDelay.TotalSeconds > 0)
                {
                    Log.Information("===========================================================================");
                    Log.Information($"Waiting to start service '{serviceConfig.Name}' at {scheduledStartTime:hh:mm tt}...");
                    await Task.Delay(remainingDelay);
                }

                Log.Information($"Starting service: {service.DisplayName}...");
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Log.Information($"Service '{service.DisplayName}' has been started successfully.");
                Log.Information("===========================================================================");
            }
            catch (Exception ex)
            {
                Log.Information("**************************************************************************************");
                Log.Error(ex, $"An error occurred while managing service '{serviceConfig.Name}': {ex.Message}");
                Log.Information("**************************************************************************************");
            }
        }

        private static async Task RestartService(RestartServiceConfig restartServiceConfig)
        {
            if (string.IsNullOrWhiteSpace(restartServiceConfig.Name))
            {
                Log.Information("**************************************************************************************");
                Log.Warning("Restart service name is missing or empty. Skipping this restart service configuration.");
                Log.Information("**************************************************************************************");
                return;
            }

            try
            {
                ServiceController service = ServiceController.GetServices()
                    .FirstOrDefault(s => s.ServiceName.Equals(restartServiceConfig.Name, StringComparison.OrdinalIgnoreCase));

                if (service == null)
                {
                    Log.Information("**************************************************************************************");
                    Log.Warning($"Restart service '{restartServiceConfig.Name}' is not available.");
                    Log.Information("**************************************************************************************");
                    return;
                }

                if (!DateTime.TryParse(restartServiceConfig.RestartTime, out DateTime restartTime))
                {
                    Log.Information("**************************************************************************************");
                    Log.Warning($"Invalid or missing restart time for restart service '{restartServiceConfig.Name}'. Skipping...");
                    Log.Information("**************************************************************************************");
                    return;
                }

                DateTime currentDate = DateTime.Now;
                DateTime scheduledRestartTime = new DateTime(currentDate.Year, currentDate.Month, currentDate.Day, restartTime.Hour, restartTime.Minute, 0);

                if (scheduledRestartTime < currentDate)
                {
                    Log.Information("==========================================================");
                    Log.Information($"Today's restart time for '{restartServiceConfig.Name}' has passed.");
                    Log.Information($"Service '{restartServiceConfig.Name}' will restart tomorrow.");
                    Log.Information("==========================================================");
                    return;
                }

                TimeSpan restartDelay = scheduledRestartTime - DateTime.Now;
                if (restartDelay.TotalSeconds > 0)
                {
                    Log.Information("===========================================================================");
                    Log.Information($"Waiting to restart service '{restartServiceConfig.Name}' at {scheduledRestartTime:hh:mm tt}...");
                    await Task.Delay(restartDelay);
                }

                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    Log.Information("===========================================================================");
                    Log.Information($"Stopping service: {service.DisplayName}...");
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    Log.Information($"Service '{service.DisplayName}' has been stopped successfully.");
                    Log.Information("===========================================================================");
                }

                Log.Information("===========================================================================");
                Log.Information($"Starting service: {service.DisplayName}...");
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

    public class ServiceConfig
    {
        public string? Name { get; set; }
        public string? StopTime { get; set; }
        public string? StartTime { get; set; }
    }

    public class RestartServiceConfig
    {
        public string? Name { get; set; }
        public string? RestartTime { get; set; }
    }
}

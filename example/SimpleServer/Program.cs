using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Extensions;
using WsRpcServer.Services;

namespace SimpleServer
{
    public enum ServerEventType
    {
        SystemStatus,
        UserActivity
    }

    public record SystemStatusEvent(string Status, DateTime Timestamp);
    public record UserActivityEvent(string Username, string Action, DateTime Timestamp);

    public class Program
    {
        public static async Task Main(string[] args)
        {
            ActivitySource.AddActivityListener(new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    Console.WriteLine($"▶️ Start: {activity.DisplayName} | TraceId: {activity.TraceId}");
                },
                ActivityStopped = activity =>
                {
                    Console.WriteLine($"⏹️ Stop:  {activity.DisplayName} | Duration: {activity.Duration}");
                }
            });
            
            // Create service collection
            var services = new ServiceCollection();

            // Configure logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Register our business service
            services.AddSingleton<ICalculatorService, CalculatorService>();

            // Add JSON-RPC core services + concrete implementations in one call.
            // The composition root now lives in the library (finding H1): no more hand-wiring
            // five services and constructing the server manually in consumer code.
            services.AddJsonRpcCore<
                DemoJsonRpcServer,
                DemoJsonRpcSession,
                DemoEventProcessor,
                DemoSubscriptionManager,
                DemoServiceRegistry,
                ServerEventType,
                object>(options =>
            {
                options.Host = "0.0.0.0";
                options.Port = 9000;
            });

            // Build service provider
            var serviceProvider = services.BuildServiceProvider();

            // Start the event processor
            var eventProcessor = serviceProvider.GetRequiredService<IEventProcessor>();
            await eventProcessor.StartAsync(CancellationToken.None);

            // Get and start the server
            var server = serviceProvider.GetRequiredService<DemoJsonRpcServer>();
            Console.WriteLine($"Starting RPC server on {server.Address}:{server.Port}");
            server.Start();

            Console.WriteLine("Server started. Press Enter to stop...");
            Console.ReadLine();

            // Stop the server
            Console.WriteLine("Stopping server...");
            server.Stop();

            // Stop the event processor
            await eventProcessor.StopAsync(CancellationToken.None);

            Console.WriteLine("Server stopped.");
        }
    }
}
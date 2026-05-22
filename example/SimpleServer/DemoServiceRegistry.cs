using Microsoft.Extensions.Logging;
using WsRpcServer.Services;

namespace SimpleServer;

public class DemoServiceRegistry(
    IServiceProvider serviceProvider,
    ILogger<DemoServiceRegistry> logger)
    : AbstractRpcServiceRegistry(serviceProvider, logger)
{
    protected override IEnumerable<string> GetAdditionalAssemblyPrefixes()
    {
        return ["SimpleServer"];
    }
}
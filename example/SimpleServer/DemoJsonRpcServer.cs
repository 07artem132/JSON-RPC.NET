using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using WsRpcServer.Core;

namespace SimpleServer;

public class DemoJsonRpcServer(
    IPAddress address,
    int port,
    IServiceProvider serviceProvider,
    ILogger<DemoJsonRpcServer> logger)
    : AbstractJsonRpcServer(address, port, serviceProvider, logger)
{
    protected override WsSession CreateJsonRpcSession()
    {
        return ActivatorUtilities.CreateInstance<DemoJsonRpcSession>(ServiceProvider, this);
    }
}

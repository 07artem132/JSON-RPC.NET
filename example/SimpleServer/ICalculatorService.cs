using WsRpcServer.Services;

namespace SimpleServer;

public interface ICalculatorService : IRpcService
{
    Task<int> Add(int a, int b);
    Task<int> Subtract(int a, int b);
}

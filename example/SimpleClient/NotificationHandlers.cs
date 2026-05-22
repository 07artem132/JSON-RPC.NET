using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace SimpleClient;

public class NotificationHandlers(DemoClient client, ILogger logger, JsonSerializerOptions options)
{
    private readonly ILogger _logger = logger;

    [JsonRpcMethod("onSystemStatus")]
    public void OnSystemStatus(JsonElement data)
    {
        var evt = data.Deserialize<SystemStatusEvent>(options);
        if (evt != null)
        {
            client.RaiseSystemStatus(evt); // ✅
        }
    }

    [JsonRpcMethod("onUserActivity")]
    public void OnUserActivity(JsonElement data)
    {
        var evt = data.Deserialize<UserActivityEvent>(options);
        if (evt != null)
        {
            client.RaiseUserActivity(evt); // ✅
        }
    }
}
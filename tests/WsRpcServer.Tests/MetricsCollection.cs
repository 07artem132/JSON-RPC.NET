using Xunit;

namespace WsRpcServer.Tests;

/// <summary>
/// Колекція для тестів, що слухають глобальний <c>Meter</c> "WsRpcServer". Спільна колекція серіалізує
/// їх між собою, усуваючи cross-talk вимірів (інший тест, що емітить ті самі інструменти паралельно,
/// інакше потрапив би в чужий <c>MeterListener</c>).
/// </summary>
[CollectionDefinition("WsRpcServerMetrics", DisableParallelization = true)]
public sealed class WsRpcServerMetricsTestGroup;

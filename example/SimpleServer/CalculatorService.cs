using Microsoft.Extensions.Logging;

namespace SimpleServer;

public class CalculatorService(ILogger<CalculatorService> logger) : ICalculatorService
{
    private readonly ILogger<CalculatorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    
    public Task<int> Add(int a, int b)
    {
        _logger.LogInformation("Add: {A} + {B}", a, b);
        return Task.FromResult(a + b);
    }

    public Task<int> Subtract(int a, int b)
    {
        _logger.LogInformation("Subtract: {A} - {B}", a, b);
        return Task.FromResult(a - b);
    }
}
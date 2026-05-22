using StreamJsonRpc;

namespace SimpleClient;


/// <summary>
/// Interface for server calculator service.
/// </summary>
public interface ICalculatorService
{
    /// <summary>
    /// Adds two numbers.
    /// </summary>
    /// <param name="a">First number.</param>
    /// <param name="b">Second number.</param>
    /// <returns>Sum of the numbers.</returns>
    [JsonRpcMethod("add")]
    Task<int> Add(int a, int b);

    /// <summary>
    /// Subtracts one number from another.
    /// </summary>
    /// <param name="a">First number.</param>
    /// <param name="b">Number to subtract.</param>
    /// <returns>Difference of the numbers.</returns>
    [JsonRpcMethod("subtract")]
    Task<int> Subtract(int a, int b);
}

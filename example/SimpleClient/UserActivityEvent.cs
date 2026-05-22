namespace SimpleClient;

/// <summary>
/// User activity event data.
/// </summary>
public record UserActivityEvent(string Username, string Action, DateTime Timestamp);

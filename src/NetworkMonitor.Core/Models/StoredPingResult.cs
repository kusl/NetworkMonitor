namespace NetworkMonitor.Core.Models;

/// <summary>
/// A persisted ping row read back from local storage, including its database
/// row id. Used by the remote sync feature to page through un-synced history.
/// </summary>
/// <param name="Id">Auto-increment row id from the local database.</param>
/// <param name="Target">Address that was pinged (IP or hostname).</param>
/// <param name="TargetName">Friendly name of the target, if known.</param>
/// <param name="TargetType">Category: router, internet, custom, service.</param>
/// <param name="Success">Whether the (aggregated) ping succeeded.</param>
/// <param name="RoundtripMs">Round-trip time in ms, or null if it failed.</param>
/// <param name="PacketLossPercent">Packet loss percentage for the cycle (0-100).</param>
/// <param name="Timestamp">When the check was performed (UTC).</param>
/// <param name="ErrorMessage">Error message if the ping failed.</param>
public sealed record StoredPingResult(
    long Id,
    string Target,
    string? TargetName,
    string TargetType,
    bool Success,
    long? RoundtripMs,
    double PacketLossPercent,
    DateTimeOffset Timestamp,
    string? ErrorMessage);

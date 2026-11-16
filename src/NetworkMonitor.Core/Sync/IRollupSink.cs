using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Sync;

public interface IRollupSink
{
    string Name { get; }

    string Destination { get; }

    string CheckpointKey { get; }

    RollupSyncOptions Options { get; }

    bool IsEnabled { get; }

    Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<bool> PushAsync(IReadOnlyList<CheckRollup> rows, long syncedAtMs, CancellationToken cancellationToken);
}

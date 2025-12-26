using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake internet target provider for testing.
/// </summary>
public sealed class FakeInternetTargetProvider : IInternetTargetProvider
{
    private readonly List<string> _targets = ["8.8.8.8", "1.1.1.1"];

    public string PrimaryTarget => _targets[0];

    public FakeInternetTargetProvider WithTargets(params string[] targets)
    {
        _targets.Clear();
        _targets.AddRange(targets);
        return this;
    }

    public IReadOnlyList<string> GetTargets() => _targets;
}

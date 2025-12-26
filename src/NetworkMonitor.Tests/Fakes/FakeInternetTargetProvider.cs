using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake internet target provider for testing.
/// </summary>
public sealed class FakeInternetTargetProvider : IInternetTargetProvider
{
    private string _primaryTarget = "8.8.8.8";
    private readonly List<string> _targets = ["8.8.8.8", "1.1.1.1"];

    public string PrimaryTarget => _primaryTarget;

    public FakeInternetTargetProvider WithPrimaryTarget(string target)
    {
        _primaryTarget = target;
        return this;
    }

    public FakeInternetTargetProvider WithTargets(params string[] targets)
    {
        _targets.Clear();
        _targets.AddRange(targets);
        if (!string.IsNullOrEmpty(_primaryTarget) && !_targets.Contains(_primaryTarget))
        {
            _primaryTarget = _targets.FirstOrDefault() ?? "8.8.8.8";
        }
        return this;
    }

    public IReadOnlyList<string> GetTargets() => _targets;
}

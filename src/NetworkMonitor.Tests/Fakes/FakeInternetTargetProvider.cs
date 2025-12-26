using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake internet target provider for testing.
/// </summary>
public sealed class FakeInternetTargetProvider : IInternetTargetProvider
{
    private string _primaryTarget = "8.8.8.8";
    private List<string> _targets = ["8.8.8.8", "1.1.1.1", "208.67.222.222"];

    public string PrimaryTarget => _primaryTarget;

    public FakeInternetTargetProvider WithPrimaryTarget(string target)
    {
        _primaryTarget = target;
        // Ensure primary is first in the targets list
        if (!_targets.Contains(target))
        {
            _targets.Insert(0, target);
        }
        else
        {
            _targets.Remove(target);
            _targets.Insert(0, target);
        }
        return this;
    }

    public FakeInternetTargetProvider WithTargets(params string[] targets)
    {
        _targets = targets.ToList();
        if (_targets.Count > 0)
        {
            _primaryTarget = _targets[0];
        }
        return this;
    }

    public IReadOnlyList<string> GetTargets() => _targets;
}

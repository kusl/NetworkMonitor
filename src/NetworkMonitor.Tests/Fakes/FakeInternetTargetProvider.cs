using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake internet target provider for testing.
/// </summary>
public sealed class FakeInternetTargetProvider : IInternetTargetProvider
{
    private string _primaryTarget = "8.8.8.8";
    private List<string> _targets = ["8.8.8.8", "1.1.1.1", "208.67.222.222"];
    private List<string> _ipv6Targets = ["2001:4860:4860::8888", "2606:4700:4700::1111"];

    public string PrimaryTarget => _primaryTarget;

    public FakeInternetTargetProvider WithPrimaryTarget(string target)
    {
        _primaryTarget = target;

        // Remove the target if it exists (no need to check Contains first)
        _targets.Remove(target);

        // Now insert it at the start
        _targets.Insert(0, target);

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

    public FakeInternetTargetProvider WithIPv6Targets(params string[] targets)
    {
        _ipv6Targets = targets.ToList();
        return this;
    }

    public IReadOnlyList<string> GetTargets() => _targets;

    public IReadOnlyList<string> GetIPv6Targets() => _ipv6Targets;
}

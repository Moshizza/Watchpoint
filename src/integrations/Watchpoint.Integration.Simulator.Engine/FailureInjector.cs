namespace Watchpoint.Integration.Simulator.Engine;

public sealed class FailureInjector
{
    private volatile IReadOnlyList<FailureRule> _rules;

    public FailureInjector(IReadOnlyList<FailureRule>? rules = null)
    {
        _rules = rules ?? [];
    }

    public void SetRules(IReadOnlyList<FailureRule> rules)
    {
        _rules = rules ?? [];
    }

    public FailureMode? CheckFailure(string command, Random random)
    {
        var rules = _rules;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (string.Equals(rule.Command, command, StringComparison.OrdinalIgnoreCase) || rule.Command == "*")
            {
                if (rule.Probability >= 1.0 || random.NextDouble() < rule.Probability)
                {
                    return rule.Mode;
                }
            }
        }

        return null;
    }
}

namespace Restocker.Execution;

public enum ContextMenuRetryDecision
{
    Retry,
    Skip,
    Stop,
}

/// <summary>Fork: counts adjust-price menu retries per row and consecutive failed rows per run.</summary>
public sealed class ContextMenuRetryPolicy
{
    public const int MaxRetries = 2;
    private const int MaxConsecutiveFailures = 3;

    public int RetryCount { get; private set; }
    private int consecutiveFailures;

    public ContextMenuRetryDecision OnEntryMissing()
    {
        if (RetryCount < MaxRetries)
        {
            RetryCount++;
            return ContextMenuRetryDecision.Retry;
        }

        RetryCount = 0;
        consecutiveFailures++;
        return consecutiveFailures >= MaxConsecutiveFailures
            ? ContextMenuRetryDecision.Stop
            : ContextMenuRetryDecision.Skip;
    }

    public void Reset()
    {
        RetryCount = 0;
        consecutiveFailures = 0;
    }
}

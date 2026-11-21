namespace Restocker.Execution;

public static class SaleHistoryPolicy
{
    public static string? SkipReason(ExecutionMode mode, bool enabled, bool alreadyRequestedThisRun)
    {
        if (mode is not (ExecutionMode.ApplyActions or ExecutionMode.RefreshAll or ExecutionMode.FullCycle))
            return $"mode={mode}";
        if (!enabled) return "toggle off";
        return alreadyRequestedThisRun ? "already requested this run" : null;
    }
}

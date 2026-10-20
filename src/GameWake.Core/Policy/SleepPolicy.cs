using GameWake.Core.Models;
using GameWake.Core.Settings;

namespace GameWake.Core.Policy;

public static class SleepPolicy
{
    /// <summary>
    /// Whether an operation justifies preventing sleep. Requires a working state the user
    /// opted into, and confirmed evidence (or probable evidence when explicitly allowed).
    /// Simulated activity never qualifies unless the developer switch allows it.
    /// </summary>
    public static bool Qualifies(GameOperation operation, AppSettings settings, bool simulationCanPreventSleep)
    {
        if (operation.IsSimulated && !simulationCanPreventSleep)
        {
            return false;
        }

        if (!operation.State.IsWorking() || !settings.PreventsSleepDuring(operation.State))
        {
            return false;
        }

        return operation.Confidence switch
        {
            DetectionConfidence.Confirmed => true,
            DetectionConfidence.Probable => settings.IncludeProbableActivity,
            _ => false,
        };
    }
}

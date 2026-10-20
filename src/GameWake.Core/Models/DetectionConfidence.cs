namespace GameWake.Core.Models;

/// <summary>
/// How certain GameWake is about the reported state. Values are ordered so that
/// a larger value means stronger evidence of an active operation.
/// </summary>
public enum DetectionConfidence
{
    /// <summary>Reliable evidence that nothing is being transferred or installed.</summary>
    Inactive = 0,

    /// <summary>Insufficient, stale or contradictory evidence.</summary>
    Unknown = 1,

    /// <summary>Several independent indirect signals agree.</summary>
    Probable = 2,

    /// <summary>The launcher or Windows directly reports the operation.</summary>
    Confirmed = 3,
}

public enum EvidenceKind
{
    /// <summary>The launcher or Windows states the activity explicitly.</summary>
    Direct,

    /// <summary>The activity is inferred from side effects such as disk writes.</summary>
    Inferred,
}

public static class DetectionConfidenceExtensions
{
    public static string ToDisplayText(this DetectionConfidence confidence) => confidence switch
    {
        DetectionConfidence.Confirmed => "Confirmed",
        DetectionConfidence.Probable => "Probable",
        DetectionConfidence.Inactive => "Inactive",
        _ => "Unknown",
    };
}

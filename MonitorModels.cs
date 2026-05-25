using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CodexBarWin;

public sealed class MonitorStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("snapshot")]
    public MonitorSnapshot? Snapshot { get; set; }

    [JsonPropertyName("events")]
    public List<MonitorEvent>? Events { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("consecutiveFailures")]
    public int ConsecutiveFailures { get; set; }
}

public sealed class MonitorSnapshot
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("pollTimeLocal")]
    public string? PollTimeLocal { get; set; }

    [JsonPropertyName("pollTimeUtc")]
    public string? PollTimeUtc { get; set; }

    [JsonPropertyName("updatedAtLocal")]
    public string? UpdatedAtLocal { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public string? UpdatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtAgeSeconds")]
    public double UpdatedAtAgeSeconds { get; set; }

    [JsonPropertyName("primary")]
    public UsageWindow? Primary { get; set; }

    [JsonPropertyName("secondary")]
    public UsageWindow? Secondary { get; set; }

    [JsonPropertyName("creditsRemaining")]
    public int? CreditsRemaining { get; set; }

    [JsonPropertyName("limitReached")]
    public string? LimitReached { get; set; }

    [JsonPropertyName("accountLabel")]
    public string? AccountLabel { get; set; }

    [JsonPropertyName("accountKind")]
    public string? AccountKind { get; set; }

    [JsonPropertyName("accountPlanType")]
    public string? AccountPlanType { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("modelDisplayName")]
    public string? ModelDisplayName { get; set; }

    [JsonPropertyName("rateLimitBuckets")]
    public List<RateLimitBucket> RateLimitBuckets { get; set; } = [];

    [JsonPropertyName("usageEstimate")]
    public LocalUsageEstimate? UsageEstimate { get; set; }
}

public sealed class UsageWindow
{
    [JsonPropertyName("usedPercent")]
    public double UsedPercent { get; set; }

    [JsonPropertyName("remainingPercent")]
    public double RemainingPercent { get; set; }

    [JsonPropertyName("windowMinutes")]
    public int WindowMinutes { get; set; }

    [JsonPropertyName("resetsAtUtc")]
    public string? ResetsAtUtc { get; set; }

    [JsonPropertyName("resetsAtLocal")]
    public string? ResetsAtLocal { get; set; }
}

public sealed class RateLimitBucket
{
    [JsonPropertyName("limitId")]
    public string? LimitId { get; set; }

    [JsonPropertyName("limitName")]
    public string? LimitName { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; set; }

    [JsonPropertyName("primary")]
    public UsageWindow? Primary { get; set; }

    [JsonPropertyName("secondary")]
    public UsageWindow? Secondary { get; set; }

    [JsonPropertyName("credits")]
    public UsageCredits? Credits { get; set; }

    [JsonPropertyName("limitReached")]
    public string? LimitReached { get; set; }
}

public sealed class UsageCredits
{
    [JsonPropertyName("hasCredits")]
    public bool HasCredits { get; set; }

    [JsonPropertyName("unlimited")]
    public bool Unlimited { get; set; }

    [JsonPropertyName("balance")]
    public string? Balance { get; set; }
}

public sealed class LocalUsageEstimate
{
    [JsonPropertyName("todayTokens")]
    public long? TodayTokens { get; set; }

    [JsonPropertyName("thirtyDayTokens")]
    public long? ThirtyDayTokens { get; set; }

    [JsonPropertyName("latestTokens")]
    public long? LatestTokens { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("costEstimate")]
    public string? CostEstimate { get; set; }

    [JsonPropertyName("costUnavailableReason")]
    public string? CostUnavailableReason { get; set; }
}

public sealed class MonitorEvent
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}



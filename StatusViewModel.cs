using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CodexBarWin;

public sealed class StatusViewModel : INotifyPropertyChanged
{
    private const double MeterTrackWidth = 294;
    private const double MeterBeadWidth = 14;

    private MonitorStatus? _status;
    private string? _fallbackText;
    private bool _isRefreshing;
    private bool _isDarkTheme;

    public event PropertyChangedEventHandler? PropertyChanged;

    public StatusViewModel()
    {
        SetFallback("Loading Codex status...");
    }

    public string Tooltip
    {
        get
        {
            if (_status?.Snapshot is null) return $"CodexBarWin: {StateBadge}";

            var bucket = UsageBuckets.FirstOrDefault();
            if (bucket is null) return $"CodexBarWin: {StateBadge}";

            var suffix = StateBadge == "HEALTHY" ? "" : $" ({StateDisplayLabel})";
            return $"Codex: {bucket.Primary?.RemainingLabel ?? "--"} session / {bucket.Secondary?.RemainingLabel ?? "--"} weekly{suffix}";
        }
    }

    public string HeaderDetail => _status?.Snapshot is not null ? "Windows Codex usage" : "Looking for Codex";

    public string HeaderSubtitle => _status?.Snapshot?.AccountLabel
        ?? _status?.Snapshot?.AccountKind
        ?? "Looking for Codex";

    public string UpdatedLine => _status?.Snapshot is not null
        ? $"Updated {UpdatedSummary}"
        : "Waiting for first refresh";

    public IReadOnlyList<DetailChipViewModel> DetailItems => BuildDetailItems();

    public IReadOnlyList<LimitBucketViewModel> UsageBuckets => BuildUsageBuckets();

    public ScrollMode UsageScrollMode => UsageBuckets.Count > 2 ? ScrollMode.Enabled : ScrollMode.Disabled;

    public string StateBadge
    {
        get
        {
            if (_isRefreshing) return "REFRESHING";
            if (_status is null) return "LOADING";
            if (!_status.Ok) return "ERROR";
            if (IsStale) return "STALE";
            var minimumRemaining = UsageBuckets
                .SelectMany(bucket => new[] { bucket.Primary, bucket.Secondary })
                .Where(row => row is not null)
                .Select(row => row!.RemainingPercent)
                .DefaultIfEmpty(100)
                .Min();
            if (minimumRemaining <= 5) return "LOW";
            return "HEALTHY";
        }
    }

    public string StateDisplayLabel => StateBadge switch
    {
        "HEALTHY" => "Healthy",
        "LOW" => "Low",
        "ERROR" => "Error",
        "STALE" => "Stale",
        "REFRESHING" => "Refreshing",
        "LOADING" => "Loading",
        _ => StateBadge
    };

    public SolidColorBrush StateBadgeBrush => StateBadge switch
    {
        "HEALTHY" => Brush("#2438D9A9"),
        "LOW" => Brush("#33FFB454"),
        "ERROR" => Brush("#40FF5C7A"),
        "STALE" => Brush("#337BA7FF"),
        "REFRESHING" => Brush("#338FD7FF"),
        _ => Brush("#24333B48"),
    };

    public SolidColorBrush StateBadgeForeground => StateBadge switch
    {
        "HEALTHY" => Brush("#FF9FFFEA"),
        "LOW" => Brush("#FFFFD8A6"),
        "ERROR" => Brush("#FFFF9DAE"),
        "STALE" => Brush("#FFBFD3FF"),
        "REFRESHING" => Brush("#FFC6EDFF"),
        _ => Brush("#FFDDE5F2"),
    };

    public SolidColorBrush StateAccentBrush => StateBadge switch
    {
        "HEALTHY" => Brush(_isDarkTheme ? "#FF6EE9DE" : "#FF54E2D5"),
        "LOW" => Brush(_isDarkTheme ? "#FFFFD08A" : "#FFF7C98F"),
        "ERROR" => Brush(_isDarkTheme ? "#FFFF96AF" : "#FFFF7E9D"),
        "STALE" => Brush(_isDarkTheme ? "#FFAFC6FF" : "#FF91B5FF"),
        "REFRESHING" => Brush(_isDarkTheme ? "#FFAFC6FF" : "#FF91B5FF"),
        _ => Brush(_isDarkTheme ? "#FFD4C6FF" : "#FFA8A2C7"),
    };

    public SolidColorBrush StatePillBrush => StateBadge switch
    {
        "ERROR" => Brush(_isDarkTheme ? "#2CFF96AF" : "#9BFFFFFF"),
        "LOW" => Brush(_isDarkTheme ? "#2CFFD08A" : "#90FFFFFF"),
        _ => Brush(_isDarkTheme ? "#24FFFFFF" : "#9BFFFFFF"),
    };

    public SolidColorBrush StatePillForeground => StateBadge switch
    {
        "HEALTHY" => Brush(_isDarkTheme ? "#FFB8FFF8" : "#FF176F72"),
        "LOW" => Brush(_isDarkTheme ? "#FFFFE4B6" : "#FF8B5A17"),
        "ERROR" => Brush(_isDarkTheme ? "#FFFFCAD6" : "#FF9B354D"),
        "STALE" => Brush(_isDarkTheme ? "#FFD6E0FF" : "#FF51689E"),
        "REFRESHING" => Brush(_isDarkTheme ? "#FFD6E0FF" : "#FF51689E"),
        _ => Brush(_isDarkTheme ? "#FFE8DFFF" : "#FF71698D"),
    };

    public double PrimaryRemaining => _status?.Snapshot?.Primary?.RemainingPercent ?? 0;
    public double WeeklyRemaining => _status?.Snapshot?.Secondary?.RemainingPercent ?? 0;
    public double PrimaryUsedPercent => _status?.Snapshot?.Primary?.UsedPercent ?? 0;
    public double WeeklyUsedPercent => _status?.Snapshot?.Secondary?.UsedPercent ?? 0;
    public double PrimaryMeterFillWidth => MeterFillWidth(PrimaryUsedPercent);
    public double WeeklyMeterFillWidth => MeterFillWidth(WeeklyUsedPercent);
    public Thickness PrimaryMeterBeadMargin => MeterBeadMargin(PrimaryUsedPercent);
    public Thickness WeeklyMeterBeadMargin => MeterBeadMargin(WeeklyUsedPercent);

    public string PrimaryLabel => _status?.Snapshot?.Primary is null ? "--" : $"{PrimaryRemaining:0}% left";
    public string WeeklyLabel => _status?.Snapshot?.Secondary is null ? "--" : $"{WeeklyRemaining:0}% left";
    public string PrimaryUsedLabel => _status?.Snapshot?.Primary is null ? "--" : $"{PrimaryUsedPercent:0}% used";
    public string WeeklyUsedLabel => _status?.Snapshot?.Secondary is null ? "--" : $"{WeeklyUsedPercent:0}% used";

    public string PrimaryResetLine => ResetLine("Resets", _status?.Snapshot?.Primary?.ResetsAtUtc);
    public string WeeklyResetLine => ResetLine("Resets", _status?.Snapshot?.Secondary?.ResetsAtUtc);
    public string PrimaryResetCompact => ResetLineCompact("Resets", _status?.Snapshot?.Primary?.ResetsAtUtc);
    public string WeeklyResetCompact => ResetLineCompact("Resets", _status?.Snapshot?.Secondary?.ResetsAtUtc);

    public SolidColorBrush PrimaryMeterBrush => Brush(_isDarkTheme ? "#FFFFD08A" : "#FFFFC978");
    public SolidColorBrush PrimaryMeterSoftBrush => Brush(_isDarkTheme ? "#FFFFD08A" : "#FFFFC978");
    public SolidColorBrush WeeklyMeterBrush => Brush(_isDarkTheme ? "#FF75E6DD" : "#FF6DDED8");
    public SolidColorBrush WeeklyMeterSoftBrush => Brush(_isDarkTheme ? "#FF75E6DD" : "#FF6DDED8");

    public string FooterStatusLabel => StateBadge switch
    {
        "ERROR" => "Needs attention",
        "LOW" => "Quota low",
        "STALE" => "Stale data",
        "REFRESHING" => "Refreshing",
        "LOADING" => "Waiting",
        _ => "Live data"
    };

    public string FooterDetail => _isRefreshing ? "Checking now" : $"Updated {UpdatedSummary}";

    public string UpdatedSummary
    {
        get
        {
            if (_isRefreshing) return "Refreshing...";
            return RelativeTime(_status?.Snapshot?.UpdatedAtUtc);
        }
    }

    public string UpdatedDetail => UpdatedDetailText(_status?.Snapshot?.UpdatedAtUtc);

    public string AttentionLine
    {
        get
        {
            if (_status?.Ok == false) return _status.Error ?? "Refresh failed.";
            if (!string.IsNullOrWhiteSpace(_status?.Snapshot?.LimitReached))
            {
                return FriendlyLimitReached(_status.Snapshot.LimitReached);
            }
            if (_status?.Events?.Count > 0)
            {
                return string.Join(" ", _status.Events.ConvertAll(e => e.Message).FindAll(m => !string.IsNullOrWhiteSpace(m)));
            }
            return _fallbackText is null || _status?.Ok == true ? "" : _fallbackText;
        }
    }

    public Visibility AttentionVisibility => string.IsNullOrWhiteSpace(AttentionLine)
        ? Visibility.Collapsed
        : Visibility.Visible;

    private bool IsStale
    {
        get
        {
            var poll = ParseUtc(_status?.Snapshot?.PollTimeUtc);
            return poll is not null && DateTimeOffset.UtcNow - poll.Value > TimeSpan.FromHours(1);
        }
    }

    public void SetRefreshing(bool value)
    {
        _isRefreshing = value;
        RaiseAll();
    }

    public void SetTheme(bool isDarkTheme)
    {
        _isDarkTheme = isDarkTheme;
        RaiseAll();
    }

    public void SetStatus(MonitorStatus status)
    {
        _status = status;
        _fallbackText = "";
        RaiseAll();
    }

    public void SetFallback(string text)
    {
        _fallbackText = text;
        RaiseAll();
    }

    public void RefreshCountdowns()
    {
        RaiseAll();
    }

    private IReadOnlyList<DetailChipViewModel> BuildDetailItems()
    {
        var snapshot = _status?.Snapshot;
        if (snapshot is null)
        {
            return
            [
                new("Plan", "Unknown"),
                new("Type", "Unknown"),
                new("Model", "Unknown"),
                new("Estimate", "Waiting")
            ];
        }

        var primaryBucket = snapshot.RateLimitBuckets?.Count > 0 ? snapshot.RateLimitBuckets[0] : null;
        var items = new List<DetailChipViewModel>();
        AddDetail(items, "Plan", DisplayPlan(snapshot.AccountPlanType) ?? DisplayAccountKind(snapshot.AccountKind));
        AddDetail(items, "Type", DisplayPlan(primaryBucket?.PlanType) ?? DisplayPlan(snapshot.AccountPlanType));
        AddDetail(items, "Model", snapshot.ModelDisplayName ?? DisplayModelName(snapshot.ModelId));
        AddDetail(items, "Tokens", EstimateSummaryValue(snapshot.UsageEstimate));
        return items;
    }

    private IReadOnlyList<LimitBucketViewModel> BuildUsageBuckets()
    {
        var snapshot = _status?.Snapshot;
        if (snapshot is null) return [];

        var buckets = snapshot.RateLimitBuckets is { Count: > 0 }
            ? snapshot.RateLimitBuckets
            : [
                new RateLimitBucket
                {
                    DisplayName = "Codex",
                    PlanType = snapshot.AccountPlanType,
                    Primary = snapshot.Primary,
                    Secondary = snapshot.Secondary,
                    LimitReached = snapshot.LimitReached
                }
            ];

        return buckets.Select(ToBucketViewModel).ToList();
    }

    private LimitBucketViewModel ToBucketViewModel(RateLimitBucket bucket)
    {
        var primary = bucket.Primary is null ? null : ToMeterRow("Session", bucket.Primary, PrimaryMeterBrush, PrimaryMeterSoftBrush);
        var secondary = bucket.Secondary is null ? null : ToMeterRow("Weekly", bucket.Secondary, WeeklyMeterBrush, WeeklyMeterSoftBrush);
        var credits = CreditsSummary(bucket.Credits);
        var planLabel = DisplayPlan(bucket.PlanType);
        var limitLine = string.IsNullOrWhiteSpace(bucket.LimitReached) ? "" : FriendlyLimitReached(bucket.LimitReached);

        return new LimitBucketViewModel(
            bucket.DisplayName ?? DisplayLimitName(bucket.LimitName, bucket.LimitId),
            planLabel ?? "",
            primary,
            secondary,
            credits,
            limitLine);
    }

    private MeterRowViewModel ToMeterRow(string title, UsageWindow window, SolidColorBrush fill, SolidColorBrush bead)
    {
        return new MeterRowViewModel(
            title,
            $"{window.UsedPercent:0}% used",
            $"{window.RemainingPercent:0}% left",
            ResetLineCompact("Resets", window.ResetsAtUtc),
            Math.Clamp(window.UsedPercent, 0, 100),
            Math.Clamp(window.RemainingPercent, 0, 100),
            MeterTrackWidth,
            MeterFillWidth(window.UsedPercent),
            MeterBeadMargin(window.UsedPercent),
            fill,
            bead);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var color = new Windows.UI.Color
        {
            A = Convert.ToByte(hex.Substring(1, 2), 16),
            R = Convert.ToByte(hex.Substring(3, 2), 16),
            G = Convert.ToByte(hex.Substring(5, 2), 16),
            B = Convert.ToByte(hex.Substring(7, 2), 16)
        };
        return new SolidColorBrush(color);
    }

    private static string ResetLine(string label, string? utc)
    {
        var reset = ParseUtc(utc);
        if (reset is null) return $"{label}: unknown";
        var delta = reset.Value - DateTimeOffset.UtcNow;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        return $"{label} in {FormatDuration(delta)} - {reset.Value.ToLocalTime():MMM d, h:mm tt}";
    }

    private static string ResetLineCompact(string label, string? utc)
    {
        var reset = ParseUtc(utc);
        if (reset is null) return $"{label}: unknown";
        var delta = reset.Value - DateTimeOffset.UtcNow;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        return $"{label} in {FormatDuration(delta)}";
    }

    private static string RelativeTime(string? utc)
    {
        var time = ParseUtc(utc);
        if (time is null) return "Not updated yet";
        var delta = DateTimeOffset.UtcNow - time.Value;
        if (delta.TotalSeconds < 90) return $"{Math.Max(0, (int)delta.TotalSeconds)}s ago";
        if (delta.TotalMinutes < 90) return $"{(int)delta.TotalMinutes}m ago";
        return $"{(int)delta.TotalHours}h ago";
    }

    private static string UpdatedDetailText(string? utc)
    {
        var time = ParseUtc(utc);
        return time is null ? "Waiting for first poll" : time.Value.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ParseUtc(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}d {value.Hours}h";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}h {value.Minutes}m";
        return $"{Math.Max(0, value.Minutes)}m";
    }

    private static string? DisplayPlan(string? value)
    {
        return value switch
        {
            null or "" => null,
            "free" => "Free",
            "go" => "Go",
            "plus" => "Plus",
            "pro" => "Pro",
            "prolite" => "Pro Lite",
            "team" => "Team",
            "business" => "Business",
            "self_serve_business_usage_based" => "Business Usage",
            "enterprise" => "Enterprise",
            "enterprise_cbp_usage_based" => "Enterprise Usage",
            "edu" => "Edu",
            "unknown" => "Unknown",
            _ => DisplaySnakeCase(value)
        };
    }

    private static string? DisplayAccountKind(string? value)
    {
        return value switch
        {
            null or "" => null,
            "chatgpt" => "ChatGPT",
            "apiKey" => "API Key",
            "amazonBedrock" => "Amazon Bedrock",
            _ => DisplaySnakeCase(value)
        };
    }

    private static void AddDetail(List<DetailChipViewModel> items, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            items.Add(new DetailChipViewModel(label, value));
        }
    }

    private static string? EstimateSummaryValue(LocalUsageEstimate? estimate)
    {
        if (estimate is null) return null;
        if (!string.IsNullOrWhiteSpace(estimate.CostEstimate) && estimate.ThirtyDayTokens is > 0)
        {
            return $"{estimate.CostEstimate} - {FormatTokens(estimate.ThirtyDayTokens.Value)} last 30d";
        }
        if (estimate.ThirtyDayTokens is > 0) return $"{FormatTokens(estimate.ThirtyDayTokens.Value)} last 30d";
        if (estimate.TodayTokens is > 0) return $"{FormatTokens(estimate.TodayTokens.Value)} today";
        if (estimate.LatestTokens is > 0) return $"{FormatTokens(estimate.LatestTokens.Value)} latest";
        return null;
    }

    private static string CreditsSummary(UsageCredits? credits)
    {
        if (credits is null) return "";
        if (credits.Unlimited) return "Unlimited";
        if (!string.IsNullOrWhiteSpace(credits.Balance)) return credits.Balance;
        return credits.HasCredits ? "Available" : "None";
    }

    private static string FriendlyLimitReached(string? value)
    {
        return value switch
        {
            "rate_limit_reached" => "Rate limit reached.",
            "workspace_owner_credits_depleted" => "Workspace owner credits depleted.",
            "workspace_member_credits_depleted" => "Workspace member credits depleted.",
            "workspace_owner_usage_limit_reached" => "Workspace owner usage limit reached.",
            "workspace_member_usage_limit_reached" => "Workspace member usage limit reached.",
            null or "" => "",
            _ => $"{DisplaySnakeCase(value)}."
        };
    }

    private static string DisplayLimitName(string? limitName, string? limitId)
    {
        if (!string.IsNullOrWhiteSpace(limitName)) return limitName;
        if (!string.IsNullOrWhiteSpace(limitId)) return DisplaySnakeCase(limitId) ?? "Usage";
        return "Usage";
    }

    private static string? DisplayModelName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        return string.Join(" ", modelId.Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Equals("gpt", StringComparison.OrdinalIgnoreCase)
                ? "GPT"
                : part.ToUpperInvariant() == part
                    ? part
                    : Capitalize(part)));
    }

    private static string? DisplaySnakeCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var words = value.Replace('-', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0 ? value : string.Join(" ", words.Select(Capitalize));
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Length == 1) return value.ToUpperInvariant();
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string FormatTokens(long value)
    {
        var absolute = Math.Abs(value);
        if (absolute >= 1_000_000_000) return $"{value / 1_000_000_000d:0.#}B tok";
        if (absolute >= 1_000_000) return $"{value / 1_000_000d:0.#}M tok";
        if (absolute >= 1_000) return $"{value / 1_000d:0.#}K tok";
        return $"{value:0} tok";
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(Tooltip), nameof(HeaderDetail), nameof(HeaderSubtitle), nameof(UpdatedLine),
            nameof(DetailItems), nameof(UsageBuckets), nameof(UsageScrollMode), nameof(StateBadge),
            nameof(StateDisplayLabel), nameof(StateBadgeBrush), nameof(StateBadgeForeground),
            nameof(StateAccentBrush), nameof(StatePillBrush), nameof(StatePillForeground),
            nameof(PrimaryRemaining), nameof(WeeklyRemaining), nameof(PrimaryUsedPercent), nameof(WeeklyUsedPercent),
            nameof(PrimaryMeterFillWidth), nameof(WeeklyMeterFillWidth),
            nameof(PrimaryMeterBeadMargin), nameof(WeeklyMeterBeadMargin),
            nameof(PrimaryLabel), nameof(WeeklyLabel), nameof(PrimaryUsedLabel), nameof(WeeklyUsedLabel),
            nameof(PrimaryResetLine), nameof(WeeklyResetLine), nameof(PrimaryResetCompact), nameof(WeeklyResetCompact),
            nameof(PrimaryMeterBrush), nameof(PrimaryMeterSoftBrush), nameof(WeeklyMeterBrush), nameof(WeeklyMeterSoftBrush),
            nameof(UpdatedSummary), nameof(UpdatedDetail), nameof(FooterStatusLabel), nameof(FooterDetail),
            nameof(AttentionLine), nameof(AttentionVisibility)
        })
        {
            OnPropertyChanged(name);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static Thickness MeterBeadMargin(double usedPercent)
    {
        var clamped = double.IsFinite(usedPercent) ? Math.Clamp(usedPercent, 0, 100) : 0;
        var x = Math.Clamp(MeterTrackWidth * clamped / 100 - MeterBeadWidth / 2, 0, MeterTrackWidth - MeterBeadWidth);
        return new Thickness(x, 0, 0, 0);
    }

    private static double MeterFillWidth(double usedPercent)
    {
        var clamped = double.IsFinite(usedPercent) ? Math.Clamp(usedPercent, 0, 100) : 0;
        return MeterTrackWidth * clamped / 100;
    }
}

public sealed record DetailChipViewModel(string Label, string Value);

public sealed record LimitBucketViewModel(
    string Name,
    string PlanTypeLabel,
    MeterRowViewModel? Primary,
    MeterRowViewModel? Secondary,
    string CreditsLabel,
    string LimitReachedLabel)
{
    public Visibility PlanTypeVisibility => string.IsNullOrWhiteSpace(PlanTypeLabel) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PrimaryVisibility => Primary is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SecondaryVisibility => Secondary is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CreditsVisibility => string.IsNullOrWhiteSpace(CreditsLabel) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LimitReachedVisibility => string.IsNullOrWhiteSpace(LimitReachedLabel) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record MeterRowViewModel(
    string Title,
    string UsedLabel,
    string RemainingLabel,
    string ResetLabel,
    double UsedPercent,
    double RemainingPercent,
    double MeterTrackWidth,
    double MeterFillWidth,
    Thickness MeterBeadMargin,
    SolidColorBrush MeterBrush,
    SolidColorBrush MeterSoftBrush);

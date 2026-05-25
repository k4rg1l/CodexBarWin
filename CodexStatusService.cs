using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin;

public sealed class CodexStatusService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly StatusViewModel _viewModel;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly CodexUsageFetcher _fetcher = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly DispatcherTimer _timer;
    private MonitorSnapshot? _lastGoodSnapshot;
    private int _consecutiveFailures;
    private bool _disposed;

    public CodexStatusService(AppPaths paths, StatusViewModel viewModel)
    {
        _paths = paths;
        _viewModel = viewModel;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshNowAsync(manual: false);
    }

    public void Start()
    {
        LoadCachedStatus();
        _timer.Start();
        _ = RefreshNowAsync(manual: false);
    }

    public async Task RefreshNowAsync(bool manual)
    {
        if (_disposed) return;
        if (!await _refreshLock.WaitAsync(0)) return;

        SetRefreshing(true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(manual ? 45 : 35));
            var status = await _fetcher.FetchAsync(_lastGoodSnapshot, timeout.Token);
            if (status.Ok && status.Snapshot is not null)
            {
                _lastGoodSnapshot = status.Snapshot;
                _consecutiveFailures = 0;
            }
            else
            {
                _consecutiveFailures++;
                status.ConsecutiveFailures = _consecutiveFailures;
            }

            ApplyStatus(status);
            await PersistStatusAsync(status);
            if (!status.Ok)
            {
                await AppendLogAsync(status.Error);
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            var status = new MonitorStatus
            {
                Ok = false,
                Snapshot = _lastGoodSnapshot,
                Error = $"Refresh failed: {ex.Message}",
                ConsecutiveFailures = _consecutiveFailures
            };
            ApplyStatus(status);
            await PersistStatusAsync(status);
            await AppendLogAsync(status.Error);
        }
        finally
        {
            SetRefreshing(false);
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _refreshLock.Dispose();
    }

    private void LoadCachedStatus()
    {
        try
        {
            if (!File.Exists(_paths.StatusJson)) return;
            using var stream = File.Open(_paths.StatusJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var status = JsonSerializer.Deserialize<MonitorStatus>(stream, JsonOptions());
            if (status is null) return;
            if (status.Snapshot is not null) _lastGoodSnapshot = status.Snapshot;
            ApplyStatus(status);
        }
        catch (Exception ex)
        {
            _viewModel.SetFallback($"Cached status is unreadable: {ex.Message}");
        }
    }

    private async Task SaveStatusAsync(MonitorStatus status)
    {
        Directory.CreateDirectory(_paths.DataRoot);
        var tempPath = _paths.StatusJson + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, status, JsonOptions());
        }

        if (File.Exists(_paths.StatusJson))
        {
            File.Replace(tempPath, _paths.StatusJson, null);
        }
        else
        {
            File.Move(tempPath, _paths.StatusJson);
        }
    }

    private async Task PersistStatusAsync(MonitorStatus status)
    {
        var persistedStatus = RedactForPersistence(status);
        await SaveStatusAsync(persistedStatus);
        await AppendHistoryAsync(persistedStatus);
    }

    private async Task AppendHistoryAsync(MonitorStatus status)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                recordedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                status.Ok,
                status.Error,
                status.ConsecutiveFailures,
                status.Snapshot
            }, CompactJsonOptions());
            await File.AppendAllTextAsync(_paths.HistoryJsonl, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private async Task AppendLogAsync(string? message)
    {
        message = RedactText(message);
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(_paths.LogPath, line);
        }
        catch
        {
        }
    }

    private void ApplyStatus(MonitorStatus status)
    {
        _dispatcherQueue.TryEnqueue(() => _viewModel.SetStatus(status));
    }

    private void SetRefreshing(bool value)
    {
        _dispatcherQueue.TryEnqueue(() => _viewModel.SetRefreshing(value));
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }

    private static JsonSerializerOptions CompactJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    private static MonitorStatus RedactForPersistence(MonitorStatus status)
    {
        return new MonitorStatus
        {
            Ok = status.Ok,
            Snapshot = RedactSnapshot(status.Snapshot),
            Events = status.Events?.ConvertAll(e => new MonitorEvent { Message = RedactText(e.Message) }),
            Error = RedactText(status.Error),
            ConsecutiveFailures = status.ConsecutiveFailures
        };
    }

    private static MonitorSnapshot? RedactSnapshot(MonitorSnapshot? snapshot)
    {
        if (snapshot is null) return null;

        return new MonitorSnapshot
        {
            Version = RedactText(snapshot.Version),
            Source = RedactText(snapshot.Source),
            PollTimeLocal = snapshot.PollTimeLocal,
            PollTimeUtc = snapshot.PollTimeUtc,
            UpdatedAtLocal = snapshot.UpdatedAtLocal,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
            UpdatedAtAgeSeconds = snapshot.UpdatedAtAgeSeconds,
            Primary = CloneUsageWindow(snapshot.Primary),
            Secondary = CloneUsageWindow(snapshot.Secondary),
            CreditsRemaining = snapshot.CreditsRemaining,
            LimitReached = RedactText(snapshot.LimitReached),
            AccountLabel = LooksLikeEmail(snapshot.AccountLabel) ? null : RedactText(snapshot.AccountLabel),
            AccountKind = RedactText(snapshot.AccountKind),
            AccountPlanType = RedactText(snapshot.AccountPlanType),
            ModelId = RedactText(snapshot.ModelId),
            ModelDisplayName = RedactText(snapshot.ModelDisplayName),
            RateLimitBuckets = snapshot.RateLimitBuckets.ConvertAll(RedactBucket),
            UsageEstimate = RedactEstimate(snapshot.UsageEstimate)
        };
    }

    private static UsageWindow? CloneUsageWindow(UsageWindow? window)
    {
        if (window is null) return null;

        return new UsageWindow
        {
            UsedPercent = window.UsedPercent,
            RemainingPercent = window.RemainingPercent,
            WindowMinutes = window.WindowMinutes,
            ResetsAtUtc = window.ResetsAtUtc,
            ResetsAtLocal = window.ResetsAtLocal
        };
    }

    private static RateLimitBucket RedactBucket(RateLimitBucket bucket)
    {
        return new RateLimitBucket
        {
            LimitId = RedactText(bucket.LimitId),
            LimitName = RedactText(bucket.LimitName),
            DisplayName = RedactText(bucket.DisplayName),
            PlanType = RedactText(bucket.PlanType),
            Primary = CloneUsageWindow(bucket.Primary),
            Secondary = CloneUsageWindow(bucket.Secondary),
            Credits = bucket.Credits is null
                ? null
                : new UsageCredits
                {
                    HasCredits = bucket.Credits.HasCredits,
                    Unlimited = bucket.Credits.Unlimited,
                    Balance = RedactText(bucket.Credits.Balance)
                },
            LimitReached = RedactText(bucket.LimitReached)
        };
    }

    private static LocalUsageEstimate? RedactEstimate(LocalUsageEstimate? estimate)
    {
        if (estimate is null) return null;

        return new LocalUsageEstimate
        {
            TodayTokens = estimate.TodayTokens,
            ThirtyDayTokens = estimate.ThirtyDayTokens,
            LatestTokens = estimate.LatestTokens,
            Source = RedactText(estimate.Source),
            CostEstimate = RedactText(estimate.CostEstimate),
            CostUnavailableReason = RedactText(estimate.CostUnavailableReason)
        };
    }

    private static bool LooksLikeEmail(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && EmailPattern.IsMatch(value);
    }

    private static string? RedactText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : EmailPattern.Replace(value, "[redacted-email]");
    }
}


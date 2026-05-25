using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin;

public sealed class CodexUsageFetcher
{
    private static readonly TimeSpan AppServerTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);
    private readonly HttpClient _httpClient = new() { Timeout = HttpTimeout };

    public async Task<MonitorStatus> FetchAsync(MonitorSnapshot? lastGoodSnapshot, CancellationToken cancellationToken)
    {
        var install = WindowsCodexDiscovery.Discover();
        var errors = new List<string>();

        foreach (var command in install.Commands)
        {
            try
            {
                return await FetchViaAppServerAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{command.Source}: Codex app-server timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{command.Source}: {ex.Message}");
            }
        }

        try
        {
            return await FetchViaAuthJsonAsync(install.AuthJsonPath, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            errors.Add("auth.json: ChatGPT usage endpoint timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"auth.json: {ex.Message}");
        }

        return new MonitorStatus
        {
            Ok = false,
            Snapshot = lastGoodSnapshot,
            Error = errors.Count == 0
                ? "No Windows Codex installation was found."
                : $"Could not refresh Codex limits. {string.Join(" | ", errors.Take(4))}",
            ConsecutiveFailures = 1
        };
    }

    private static async Task<MonitorStatus> FetchViaAppServerAsync(CodexCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AppServerTimeout);

        using var process = StartAppServer(command);
        var outputLines = new List<string>();
        var outputLock = new object();
        var stdoutTask = ReadLinesAsync(process.StandardOutput, line =>
        {
            lock (outputLock)
            {
                outputLines.Add(line);
            }
        }, timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await SendAsync(process, new
            {
                method = "initialize",
                id = 1,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "CodexBarWin-win",
                        title = "CodexBarWin",
                        version = AppVersion.Value
                    }
                }
            }, timeout.Token);

            await Task.Delay(100, timeout.Token);
            await SendAsync(process, new { method = "initialized", @params = new { } }, timeout.Token);
            await Task.Delay(150, timeout.Token);
            await SendAsync(process, new { method = "account/read", id = 2, @params = new { refreshToken = true } }, timeout.Token);
            await Task.Delay(250, timeout.Token);
            await SendAsync(process, new { method = "account/rateLimits/read", id = 3, @params = new { } }, timeout.Token);
            await Task.Delay(100, timeout.Token);
            await SendAsync(process, new { method = "config/read", id = 4, @params = new { includeLayers = false } }, timeout.Token);
            await Task.Delay(100, timeout.Token);
            await SendAsync(process, new { method = "model/list", id = 5, @params = new { includeHidden = false, limit = 100 } }, timeout.Token);

            JsonElement? accountResult = null;
            JsonElement? rateLimitsResult = null;
            JsonElement? configResult = null;
            JsonElement? modelListResult = null;
            DateTimeOffset? gotRateLimitsAt = null;
            while (!timeout.IsCancellationRequested)
            {
                string[] lines;
                lock (outputLock)
                {
                    lines = outputLines.ToArray();
                }

                ExtractAppServerResults(lines, ref accountResult, ref rateLimitsResult, ref configResult, ref modelListResult);
                if (rateLimitsResult is not null)
                {
                    gotRateLimitsAt ??= DateTimeOffset.UtcNow;
                    var optionalComplete = accountResult is not null && configResult is not null && modelListResult is not null;
                    if (optionalComplete || DateTimeOffset.UtcNow - gotRateLimitsAt.Value > TimeSpan.FromMilliseconds(1500))
                    {
                        break;
                    }
                }

                if (process.HasExited) break;

                await Task.Delay(100, timeout.Token);
            }

            TryKill(process);
            await WaitForExitQuietlyAsync(process);

            var stderr = await ReadTaskQuietlyAsync(stderrTask);
            await ReadTaskQuietlyAsync(stdoutTask);
            string[] finalOutputLines;
            lock (outputLock)
            {
                finalOutputLines = outputLines.ToArray();
            }
            ExtractAppServerResults(finalOutputLines, ref accountResult, ref rateLimitsResult, ref configResult, ref modelListResult);

            if (rateLimitsResult is null)
            {
                var error = FirstInterestingError(stderr);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "Codex app-server did not return rate limits."
                    : error);
            }

            var estimate = await ReadLocalUsageEstimateAsync(timeout.Token);
            return BuildAppServerStatus(
                source: $"windows-app-server ({command.Source})",
                rateLimitsResult: rateLimitsResult.Value,
                accountResult: accountResult,
                configResult: configResult,
                modelListResult: modelListResult,
                estimate: estimate);
        }
        finally
        {
            TryKill(process);
            await WaitForExitQuietlyAsync(process);
            await ReadTaskQuietlyAsync(stdoutTask);
            await ReadTaskQuietlyAsync(stderrTask);
        }
    }

    private async Task<MonitorStatus> FetchViaAuthJsonAsync(string authJsonPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(authJsonPath)) throw new FileNotFoundException("Codex auth.json was not found.", authJsonPath);

        await using var authStream = File.Open(authJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var auth = await JsonDocument.ParseAsync(authStream, cancellationToken: cancellationToken);
        var tokens = auth.RootElement.GetProperty("tokens");
        var accessToken = tokens.GetProperty("access_token").GetString();
        var accountId = tokens.TryGetProperty("account_id", out var accountIdElement)
            ? accountIdElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Codex auth.json has no access token.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd($"CodexBarWin/{AppVersion.Value}");
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ChatGPT usage endpoint returned {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var estimate = await ReadLocalUsageEstimateAsync(cancellationToken);

        return BuildWhamStatus(
            source: "windows-auth-json",
            root: root,
            estimate: estimate);
    }

    private static Process StartAppServer(CodexCommand command)
    {
        var extension = Path.GetExtension(command.Path);
        var isCmd = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isCmd ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : command.Path,
            Arguments = isCmd
                ? $"/d /s /c \"\"{command.Path}\" app-server --listen stdio://\""
                : "app-server --listen stdio://",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Codex app-server.");
    }

    private static async Task SendAsync(Process process, object message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!string.IsNullOrWhiteSpace(line)) onLine(line);
        }
    }

    private static void ExtractAppServerResults(
        string[] lines,
        ref JsonElement? accountResult,
        ref JsonElement? rateLimitsResult,
        ref JsonElement? configResult,
        ref JsonElement? modelListResult)
    {
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                if (!root.TryGetProperty("result", out var result)) continue;

                if (id == 2 && result.TryGetProperty("account", out _))
                {
                    accountResult = result.Clone();
                }
                else if (id == 3 && result.TryGetProperty("rateLimits", out _))
                {
                    rateLimitsResult = result.Clone();
                }
                else if (id == 4 && result.TryGetProperty("config", out _))
                {
                    configResult = result.Clone();
                }
                else if (id == 5 && result.TryGetProperty("data", out _))
                {
                    modelListResult = result.Clone();
                }
            }
            catch
            {
            }
        }
    }

    private static UsageReading? ReadAppServerWindow(JsonElement rateLimits, string name)
    {
        if (!rateLimits.TryGetProperty(name, out var window) || window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new UsageReading(
            GetDouble(window, "usedPercent"),
            GetInt(window, "windowDurationMins"),
            GetNullableLong(window, "resetsAt"));
    }

    private static UsageReading? ReadWhamWindow(JsonElement rateLimit, string name)
    {
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var windowSeconds = GetInt(window, "limit_window_seconds");
        return new UsageReading(
            GetDouble(window, "used_percent"),
            windowSeconds > 0 ? windowSeconds / 60 : 0,
            GetNullableLong(window, "reset_at"));
    }

    private static MonitorStatus BuildAppServerStatus(
        string source,
        JsonElement rateLimitsResult,
        JsonElement? accountResult,
        JsonElement? configResult,
        JsonElement? modelListResult,
        LocalUsageEstimate? estimate)
    {
        var buckets = ReadAppServerBuckets(rateLimitsResult);
        var primaryBucket = buckets.FirstOrDefault();
        var (accountLabel, accountKind, accountPlanType) = ReadAppServerAccount(accountResult);
        var (modelId, modelDisplayName) = ReadAppServerModel(configResult, modelListResult);

        return CreateStatus(
            source,
            primaryBucket?.Primary,
            primaryBucket?.Secondary,
            primaryBucket?.LimitReached,
            buckets,
            accountLabel,
            accountKind,
            accountPlanType,
            modelId,
            modelDisplayName,
            estimate);
    }

    private static MonitorStatus BuildWhamStatus(string source, JsonElement root, LocalUsageEstimate? estimate)
    {
        var buckets = ReadWhamBuckets(root);
        var primaryBucket = buckets.FirstOrDefault();
        var email = GetOptionalString(root, "email");
        var accountLabel = string.IsNullOrWhiteSpace(email) ? null : email;
        var planType = GetOptionalString(root, "plan_type");

        return CreateStatus(
            source,
            primaryBucket?.Primary,
            primaryBucket?.Secondary,
            primaryBucket?.LimitReached ?? GetOptionalString(root, "rate_limit_reached_type"),
            buckets,
            accountLabel,
            string.IsNullOrWhiteSpace(email) ? null : "chatgpt",
            planType,
            null,
            null,
            estimate);
    }

    private static MonitorStatus CreateStatus(
        string source,
        UsageWindow? primary,
        UsageWindow? secondary,
        string? limitReached,
        List<RateLimitBucket> buckets,
        string? accountLabel,
        string? accountKind,
        string? accountPlanType,
        string? modelId,
        string? modelDisplayName,
        LocalUsageEstimate? estimate)
    {
        var pollTime = DateTimeOffset.UtcNow;
        var snapshot = new MonitorSnapshot
        {
            Version = AppVersion.Value,
            Source = source,
            PollTimeUtc = pollTime.ToString("O", CultureInfo.InvariantCulture),
            PollTimeLocal = pollTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            UpdatedAtUtc = pollTime.ToString("O", CultureInfo.InvariantCulture),
            UpdatedAtLocal = pollTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            UpdatedAtAgeSeconds = 0,
            Primary = primary,
            Secondary = secondary,
            CreditsRemaining = null,
            LimitReached = limitReached,
            AccountLabel = accountLabel,
            AccountKind = accountKind,
            AccountPlanType = accountPlanType,
            ModelId = modelId,
            ModelDisplayName = modelDisplayName,
            RateLimitBuckets = buckets,
            UsageEstimate = estimate
        };

        return new MonitorStatus
        {
            Ok = true,
            Snapshot = snapshot,
            Events = [],
            ConsecutiveFailures = 0
        };
    }

    private static List<RateLimitBucket> ReadAppServerBuckets(JsonElement result)
    {
        var buckets = new List<RateLimitBucket>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        JsonElement? legacy = null;

        if (result.TryGetProperty("rateLimits", out var legacyElement) &&
            legacyElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            legacy = legacyElement;
        }

        var legacyId = legacy is null ? null : GetOptionalString(legacy.Value, "limitId");
        if (legacy is not null)
        {
            var bucket = ReadAppServerBucket(legacy.Value, legacyId);
            buckets.Add(bucket);
            MarkSeen(seen, bucket);
        }

        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byId.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (legacyId is not null && property.Name.Equals(legacyId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var bucket = ReadAppServerBucket(property.Value, property.Name);
                if (IsSeen(seen, bucket)) continue;
                buckets.Add(bucket);
                MarkSeen(seen, bucket);
            }
        }

        return buckets;
    }

    private static RateLimitBucket ReadAppServerBucket(JsonElement snapshot, string? fallbackId)
    {
        var limitId = GetOptionalString(snapshot, "limitId") ?? fallbackId;
        var limitName = GetOptionalString(snapshot, "limitName");
        return new RateLimitBucket
        {
            LimitId = limitId,
            LimitName = limitName,
            DisplayName = DisplayLimitName(limitName, limitId),
            PlanType = GetOptionalString(snapshot, "planType"),
            Primary = ToUsageWindow(ReadAppServerWindow(snapshot, "primary")),
            Secondary = ToUsageWindow(ReadAppServerWindow(snapshot, "secondary")),
            Credits = ReadAppServerCredits(snapshot),
            LimitReached = GetOptionalString(snapshot, "rateLimitReachedType")
        };
    }

    private static List<RateLimitBucket> ReadWhamBuckets(JsonElement root)
    {
        var buckets = new List<RateLimitBucket>();
        var planType = GetOptionalString(root, "plan_type");
        var limitReached = GetOptionalString(root, "rate_limit_reached_type");

        if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            buckets.Add(new RateLimitBucket
            {
                LimitId = "codex",
                DisplayName = "Codex",
                PlanType = planType,
                Primary = ToUsageWindow(ReadWhamWindow(rateLimit, "primary_window")),
                Secondary = ToUsageWindow(ReadWhamWindow(rateLimit, "secondary_window")),
                Credits = ReadWhamCredits(root),
                LimitReached = limitReached
            });
        }

        if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in additional.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var limitId = GetOptionalString(item, "metered_feature") ?? GetOptionalString(item, "limit_id");
                var limitName = GetOptionalString(item, "limit_name") ?? GetOptionalString(item, "name");
                buckets.Add(new RateLimitBucket
                {
                    LimitId = limitId,
                    LimitName = limitName,
                    DisplayName = DisplayLimitName(limitName, limitId),
                    PlanType = planType,
                    Primary = ToUsageWindow(ReadWhamWindow(item, "primary_window")),
                    Secondary = ToUsageWindow(ReadWhamWindow(item, "secondary_window")),
                    Credits = null,
                    LimitReached = GetOptionalString(item, "rate_limit_reached_type")
                });
            }
        }

        if (root.TryGetProperty("code_review_rate_limit", out var codeReview) && codeReview.ValueKind == JsonValueKind.Object)
        {
            buckets.Add(new RateLimitBucket
            {
                LimitId = "code_review",
                DisplayName = "Code Review",
                PlanType = planType,
                Primary = ToUsageWindow(ReadWhamWindow(codeReview, "primary_window")),
                Secondary = ToUsageWindow(ReadWhamWindow(codeReview, "secondary_window")),
                Credits = null,
                LimitReached = GetOptionalString(codeReview, "rate_limit_reached_type")
            });
        }

        return buckets;
    }

    private static UsageCredits? ReadAppServerCredits(JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("credits", out var credits) ||
            credits.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new UsageCredits
        {
            HasCredits = GetBool(credits, "hasCredits"),
            Unlimited = GetBool(credits, "unlimited"),
            Balance = GetOptionalString(credits, "balance")
        };
    }

    private static UsageCredits? ReadWhamCredits(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var credits) ||
            credits.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new UsageCredits
        {
            HasCredits = GetBool(credits, "has_credits"),
            Unlimited = GetBool(credits, "unlimited"),
            Balance = GetOptionalString(credits, "balance")
        };
    }

    private static (string? Label, string? Kind, string? PlanType) ReadAppServerAccount(JsonElement? result)
    {
        if (result is null || !result.Value.TryGetProperty("account", out var account) ||
            account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (null, null, null);
        }

        var type = GetOptionalString(account, "type");
        if (type?.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) == true)
        {
            return (GetOptionalString(account, "email"), type, GetOptionalString(account, "planType"));
        }

        return (DisplaySnakeCase(type), type, null);
    }

    private static (string? Id, string? DisplayName) ReadAppServerModel(JsonElement? configResult, JsonElement? modelListResult)
    {
        if (configResult is null || !configResult.Value.TryGetProperty("config", out var config))
        {
            return (null, null);
        }

        var modelId = GetOptionalString(config, "model");
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return (null, null);
        }

        var displayName = FindModelDisplayName(modelListResult, modelId) ?? DisplayModelName(modelId);
        return (modelId, displayName);
    }

    private static string? FindModelDisplayName(JsonElement? modelListResult, string modelId)
    {
        if (modelListResult is null ||
            !modelListResult.Value.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var model in data.EnumerateArray())
        {
            var id = GetOptionalString(model, "id");
            var wireModel = GetOptionalString(model, "model");
            if (modelId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                modelId.Equals(wireModel, StringComparison.OrdinalIgnoreCase))
            {
                return GetOptionalString(model, "displayName") ?? DisplayModelName(modelId);
            }
        }

        return null;
    }

    private static UsageWindow? ToUsageWindow(UsageReading? reading)
    {
        if (reading is null) return null;
        var used = Math.Clamp(reading.UsedPercent, 0, 100);
        DateTimeOffset? reset = reading.ResetAtUnixSeconds is null
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(reading.ResetAtUnixSeconds.Value);
        return new UsageWindow
        {
            UsedPercent = used,
            RemainingPercent = Math.Clamp(100 - used, 0, 100),
            WindowMinutes = reading.WindowMinutes,
            ResetsAtUtc = reset?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ResetsAtLocal = reset?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
        };
    }

    private static double GetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0;
    }

    private static int GetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    }

    private static long GetLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    }

    private static long? GetNullableLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.TryGetInt64(out var result) ? result : null;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    private static string? GetOptionalString(JsonElement? element, params string[] path)
    {
        if (element is null) return null;
        return GetOptionalString(element.Value, path);
    }

    private static string? GetOptionalString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current)) return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string DisplayLimitName(string? limitName, string? limitId)
    {
        if (!string.IsNullOrWhiteSpace(limitName)) return limitName;
        if (!string.IsNullOrWhiteSpace(limitId)) return DisplaySnakeCase(limitId) ?? "Usage";
        return "Usage";
    }

    private static string? DisplaySnakeCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var words = value.Replace('-', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return value;
        return string.Join(" ", words.Select(Capitalize));
    }

    private static string DisplayModelName(string modelId)
    {
        return string.Join(" ", modelId.Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Equals("gpt", StringComparison.OrdinalIgnoreCase)
                ? "GPT"
                : part.ToUpperInvariant() == part
                    ? part
                    : Capitalize(part)));
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Length == 1) return value.ToUpperInvariant();
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static bool IsSeen(HashSet<string> seen, RateLimitBucket bucket)
    {
        var key = !string.IsNullOrWhiteSpace(bucket.LimitId) ? bucket.LimitId : bucket.DisplayName;
        return !string.IsNullOrWhiteSpace(key) && seen.Contains(key);
    }

    private static void MarkSeen(HashSet<string> seen, RateLimitBucket bucket)
    {
        if (!string.IsNullOrWhiteSpace(bucket.LimitId)) seen.Add(bucket.LimitId);
        if (!string.IsNullOrWhiteSpace(bucket.DisplayName)) seen.Add(bucket.DisplayName);
    }

    private static async Task<LocalUsageEstimate?> ReadLocalUsageEstimateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => ReadLocalUsageEstimate(cancellationToken), cancellationToken);
        }
        catch
        {
            return new LocalUsageEstimate
            {
                Source = "local Codex logs",
                CostUnavailableReason = "Local token logs unavailable"
            };
        }
    }

    private static LocalUsageEstimate? ReadLocalUsageEstimate(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return null;

        var sessionsPath = Path.Combine(home, ".codex", "sessions");
        if (!Directory.Exists(sessionsPath))
        {
            return new LocalUsageEstimate
            {
                Source = "local Codex logs",
                CostUnavailableReason = "No local session logs"
            };
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-30);
        var today = DateTimeOffset.Now.Date;
        long thirtyDayTokens = 0;
        long todayTokens = 0;
        long? latestTokens = null;
        var latestTimestamp = DateTimeOffset.MinValue;
        var foundTokenEvents = false;

        foreach (var file in Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) continue;

                foreach (var line in File.ReadLines(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("payload", out var payload) ||
                        GetOptionalString(payload, "type")?.Equals("token_count", StringComparison.OrdinalIgnoreCase) != true ||
                        !payload.TryGetProperty("info", out var info) ||
                        !info.TryGetProperty("last_token_usage", out var lastUsage))
                    {
                        continue;
                    }

                    var timestamp = ParseTimestamp(root);
                    if (timestamp is null || timestamp.Value < cutoff) continue;

                    var totalTokens = GetNullableLong(lastUsage, "total_tokens") ??
                                      GetNullableLong(lastUsage, "totalTokens");
                    if (totalTokens is null || totalTokens <= 0) continue;

                    foundTokenEvents = true;
                    thirtyDayTokens += totalTokens.Value;
                    if (timestamp.Value.ToLocalTime().Date == today)
                    {
                        todayTokens += totalTokens.Value;
                    }

                    if (timestamp.Value > latestTimestamp)
                    {
                        latestTimestamp = timestamp.Value;
                        latestTokens = totalTokens.Value;
                    }
                }
            }
            catch
            {
            }
        }

        return new LocalUsageEstimate
        {
            TodayTokens = foundTokenEvents ? todayTokens : null,
            ThirtyDayTokens = foundTokenEvents ? thirtyDayTokens : null,
            LatestTokens = latestTokens,
            Source = "local Codex logs",
            CostEstimate = null,
            CostUnavailableReason = "No trusted local cost rate"
        };
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement root)
    {
        var value = GetOptionalString(root, "timestamp");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string FirstInterestingError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return string.Empty;
        return stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Redact)
            .FirstOrDefault(line => !line.Contains("startup remote plugin sync failed", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
    }

    private static string Redact(string value)
    {
        return System.Text.RegularExpressions.Regex.Replace(value, "[A-Za-z0-9_-]{20,}", "[redacted]");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static async Task<T?> ReadTaskQuietlyAsync<T>(Task<T> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return default;
        }
    }

    private static async Task ReadTaskQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private sealed record UsageReading(double UsedPercent, int WindowMinutes, long? ResetAtUnixSeconds);
}

public static class AppVersion
{
    public static string Value { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "0.1.0";
}


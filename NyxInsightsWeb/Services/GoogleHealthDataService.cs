using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NyxInsightsWeb.Models;

namespace NyxInsightsWeb.Services
{
    public class GoogleHealthApiException : Exception
    {
        public int StatusCode { get; }
        public GoogleHealthApiException(int statusCode, string message) : base(message)
            => StatusCode = statusCode;
    }

    
    public class GoogleHealthDataService
    {
        private const string BaseUrl = "https://health.googleapis.com/v4/users/me/dataTypes";
        private const int MaxPages = 20; // safety cap on pagination loops

        private static readonly bool DebugIncludeUpstreamBody = false;

        private static readonly JsonSerializerOptions ModelJsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        private readonly IHttpClientFactory _httpClientFactory;

        public GoogleHealthDataService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        private sealed record Window(string Token, DateOnly Start, DateOnly ExclusiveEnd, int Days);
        private readonly record struct Update(DateOnly Date, Action<DailyMetrics> Apply);
        private readonly record struct HourUpdate(int Hour, Action<HourlyMetrics> Apply);

        
        private static int ZoneIndex(string? googleZone) => googleZone switch
        {
            "MODERATE" => 1,
            "VIGOROUS" => 2,
            "PEAK" => 3,
            _ => 0
        };

        public static string SerializeModelRequest(ModelRequest request) =>
            JsonSerializer.Serialize(request, ModelJsonOptions);

        
        public async Task<ModelRequestResult> BuildModelRequestAsync(
            string accessToken,
            DateOnly predictionDate,
            TimeZoneInfo timeZone,
            IReadOnlyDictionary<DateOnly, double>? surveyScores = null,
            int historyDays = 30,
            CancellationToken ct = default)
        {
            var dailyTask = GetDailyHistoryAsync(accessToken, predictionDate, historyDays + 1, ct);
            var hourlyTask = GetHourlyAsync(accessToken, predictionDate, timeZone, ct);
            await Task.WhenAll(dailyTask, hourlyTask);

            DailyHistoryResult daily = await dailyTask;
            var (hours, hourlyWarnings) = await hourlyTask;

            var warnings = new List<string>(daily.Warnings);
            warnings.AddRange(hourlyWarnings);

            DayRecord current = daily.Days[^1];
            if (hours.Count > 0)
                current.Hourly = hours;

            if (!current.Daily.HasAnyData && hours.Count == 0)
                warnings.Add("No Fitbit data was found for the prediction date.");

            var history = new List<DayRecord>();
            foreach (var day in daily.Days.Take(daily.Days.Count - 1).Reverse())
            {
                DateOnly d = DateOnly.ParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (d >= predictionDate) continue; 

                if (surveyScores != null && surveyScores.TryGetValue(d, out double score))
                    day.SurveyStressScore = score;

                if (day.Daily.HasAnyData || day.SurveyStressScore.HasValue)
                    history.Add(day);
            }

            return new ModelRequestResult
            {
                Request = new ModelRequest
                {
                    InputData = new ModelInput { Current = current, History = history }
                },
                Warnings = warnings
            };
        }

    
        public async Task<DailyHistoryResult> GetDailyHistoryAsync(
            string accessToken, DateOnly endDate, int days = 31, CancellationToken ct = default)
        {
            if (days < 1 || days > 62)
                throw new ArgumentOutOfRangeException(nameof(days));

            DateOnly start = endDate.AddDays(1 - days);
            var window = new Window(accessToken, start, endDate.AddDays(1), days);
            var warnings = new ConcurrentBag<string>();

            var tasks = new[]
            {
                RunSafe("steps", () => FetchStepsAsync(window, ct), warnings),
                RunSafe("calories", () => FetchCaloriesAsync(window, ct), warnings),
                RunSafe("active minutes", () => FetchActiveMinutesAsync(window, ct), warnings),
                RunSafe("heart-rate zones", () => FetchZonesAsync(window, ct), warnings),
                RunSafe("heart rate", () => FetchHeartRateAsync(window, ct), warnings),
                RunSafe("sleep", () => FetchSleepAsync(window, ct), warnings),
            };

            List<Update>[] results = await Task.WhenAll(tasks);

            var byDate = new Dictionary<DateOnly, DailyMetrics>();
            for (int i = 0; i < days; i++)
                byDate[start.AddDays(i)] = new DailyMetrics();

            foreach (var update in results.SelectMany(r => r))
            {
                if (byDate.TryGetValue(update.Date, out var metrics))
                    update.Apply(metrics);
            }

            
            foreach (var m in byDate.Values)
            {
                bool hasWearData = m.Steps.HasValue || m.Zone1.HasValue ||
                                   m.LightlyActiveMinutes.HasValue || m.SleepEfficiency.HasValue;
                if (!hasWearData)
                    m.Calories = null;
            }

            foreach (var m in byDate.Values)
            {
                if (m.SleepMinutes.HasValue &&
                    (m.LightlyActiveMinutes.HasValue || m.ModeratelyActiveMinutes.HasValue || m.VeryActiveMinutes.HasValue))
                {
                    m.SedentaryMinutes = Math.Max(0,
                        1440 - m.SleepMinutes.Value
                             - (m.LightlyActiveMinutes ?? 0)
                             - (m.ModeratelyActiveMinutes ?? 0)
                             - (m.VeryActiveMinutes ?? 0));
                }
            }

            return new DailyHistoryResult
            {
                Days = byDate.OrderBy(kv => kv.Key)
                    .Select(kv => new DayRecord
                    {
                        Date = kv.Key.ToString("yyyy-MM-dd"),
                        Daily = kv.Value
                    })
                    .ToList(),
                Warnings = warnings.OrderBy(w => w).ToList()
            };
        }

        private async Task<List<Update>> FetchStepsAsync(Window w, CancellationToken ct)
        {
            var updates = new List<Update>();
            await ForEachRollupPointAsync("steps", w, (date, point) =>
            {
                if (point.TryGetProperty("steps", out var steps) &&
                    steps.TryGetProperty("countSum", out var sum) &&
                    TryGetNumber(sum, out double count) && count >= 0)
                {
                    long value = (long)count;
                    updates.Add(new Update(date, m => m.Steps = value));
                }
            }, ct);
            return updates;
        }

        private async Task<List<Update>> FetchCaloriesAsync(Window w, CancellationToken ct)
        {
            var updates = new List<Update>();
            await ForEachRollupPointAsync("total-calories", w, (date, point) =>
            {
                if (point.TryGetProperty("totalCalories", out var cals) &&
                    cals.TryGetProperty("kcalSum", out var sum) &&
                    TryGetNumber(sum, out double kcal) && kcal >= 0)
                {
                    updates.Add(new Update(date, m => m.Calories = Math.Round(kcal, 1)));
                }
            }, ct, chunkDays: 7);
            return updates;
        }

        private async Task<List<Update>> FetchActiveMinutesAsync(Window w, CancellationToken ct)
        {
            var updates = new List<Update>();
            await ForEachRollupPointAsync("active-minutes", w, (date, point) =>
            {
                if (!point.TryGetProperty("activeMinutes", out var active) ||
                    !active.TryGetProperty("activeMinutesRollupByActivityLevel", out var levels) ||
                    levels.ValueKind != JsonValueKind.Array)
                    return;

                double light = 0, moderate = 0, vigorous = 0;
                foreach (var level in levels.EnumerateArray())
                {
                    if (!level.TryGetProperty("activityLevel", out var nameEl) ||
                        !level.TryGetProperty("activeMinutesSum", out var sumEl) ||
                        !TryGetNumber(sumEl, out double minutes) || minutes < 0)
                        continue;

                    switch (nameEl.GetString())
                    {
                        case "LIGHT": light += minutes; break;
                        case "MODERATE": moderate += minutes; break;
                        case "VIGOROUS": vigorous += minutes; break;
                    }
                }

                updates.Add(new Update(date, m =>
                {
                    m.LightlyActiveMinutes = light;
                    m.ModeratelyActiveMinutes = moderate;
                    m.VeryActiveMinutes = vigorous;
                }));
            }, ct, includeTime: false, chunkDays: 7);
            return updates;
        }

        private async Task<List<Update>> FetchZonesAsync(Window w, CancellationToken ct)
        {
            var updates = new List<Update>();
            await ForEachRollupPointAsync("time-in-heart-rate-zone", w, (date, point) =>
            {
                ReadZoneMinutes(point, out double z1, out double z2, out double z3, out bool found);
                if (!found) return;

                updates.Add(new Update(date, m =>
                {
                    m.Zone1 = Math.Round(z1, 1);
                    m.Zone2 = Math.Round(z2, 1);
                    m.Zone3 = Math.Round(z3, 1);
                }));
            }, ct);
            return updates;
        }

        private async Task<List<Update>> FetchHeartRateAsync(Window w, CancellationToken ct)
        {
            var updates = new List<Update>();
            await ForEachRollupPointAsync("heart-rate", w, (date, point) =>
            {
                if (point.TryGetProperty("heartRate", out var hr) &&
                    hr.TryGetProperty("beatsPerMinuteAvg", out var avg) &&
                    TryGetNumber(avg, out double bpm) && bpm > 0)
                {
                    updates.Add(new Update(date, m => m.Bpm = Math.Round(bpm, 1)));
                }
            }, ct, chunkDays: 7); 
            return updates;
        }

        private async Task<List<Update>> FetchSleepAsync(Window w, CancellationToken ct)
        {
            var asleep = new Dictionary<DateOnly, double>();
            var inPeriod = new Dictionary<DateOnly, double>();

            string filter =
                $"sleep.interval.civil_end_time >= \"{w.Start:yyyy-MM-dd}\" " +
                $"AND sleep.interval.civil_end_time < \"{w.ExclusiveEnd:yyyy-MM-dd}\"";

            string? pageToken = null;
            for (int page = 0; page < MaxPages; page++)
            {
                string url = $"{BaseUrl}/sleep/dataPoints:reconcile" +
                             $"?filter={Uri.EscapeDataString(filter)}&pageSize=100";
                if (pageToken != null)
                    url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

                string body = await SendAsync(HttpMethod.Get, url, w.Token, null, "sleep", ct);
                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("dataPoints", out var points) &&
                    points.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dp in points.EnumerateArray())
                    {
                        if (!dp.TryGetProperty("sleep", out var sleep) ||
                            !sleep.TryGetProperty("summary", out var summary) ||
                            !summary.TryGetProperty("minutesAsleep", out var asleepEl) ||
                            !summary.TryGetProperty("minutesInSleepPeriod", out var periodEl) ||
                            !TryGetNumber(asleepEl, out double a) ||
                            !TryGetNumber(periodEl, out double p) || p <= 0)
                            continue;

                        if (!TryGetSleepEndDate(sleep, out DateOnly endDate))
                            continue;

                        asleep[endDate] = asleep.GetValueOrDefault(endDate) + a;
                        inPeriod[endDate] = inPeriod.GetValueOrDefault(endDate) + p;
                    }
                }

                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next)
                    ? next.GetString() : null;
                if (string.IsNullOrEmpty(pageToken)) break;
            }

            var updates = new List<Update>();
            foreach (var (date, period) in inPeriod)
            {
                double a = asleep[date];
                double efficiency = Math.Round(a / period * 100.0, 1);
                updates.Add(new Update(date, m =>
                {
                    m.SleepEfficiency = efficiency;
                    m.SleepMinutes = a;
                }));
            }
            return updates;
        }

        
        public async Task<(List<HourlyMetrics> Hours, List<string> Warnings)> GetHourlyAsync(
            string accessToken, DateOnly date, TimeZoneInfo timeZone, CancellationToken ct = default)
        {
            DateTime startUtc = LocalMidnightToUtc(date, timeZone);
            DateTime endUtc = LocalMidnightToUtc(date.AddDays(1), timeZone);
            var warnings = new ConcurrentBag<string>();

            var tasks = new[]
            {
                RunSafe("hourly steps", () => FetchHourlyStepsAsync(accessToken, startUtc, endUtc, timeZone, ct), warnings),
                RunSafe("hourly calories", () => FetchHourlyCaloriesAsync(accessToken, startUtc, endUtc, timeZone, ct), warnings),
                RunSafe("hourly heart rate", () => FetchHourlyHeartRateAsync(accessToken, startUtc, endUtc, timeZone, ct), warnings),
                RunSafe("hourly heart-rate zones", () => FetchHourlyZonesAsync(accessToken, startUtc, endUtc, timeZone, ct), warnings),
            };

            List<HourUpdate>[] results = await Task.WhenAll(tasks);

            var byHour = new Dictionary<int, HourlyMetrics>();
            foreach (var update in results.SelectMany(r => r))
            {
                if (!byHour.TryGetValue(update.Hour, out var metrics))
                    byHour[update.Hour] = metrics = new HourlyMetrics { Hour = update.Hour };
                update.Apply(metrics);
            }

            var hours = byHour.Values
                .Where(h => h.HasAnyData)
                .OrderBy(h => h.Hour)
                .ToList();

            return (hours, warnings.OrderBy(w => w).ToList());
        }

        private async Task<List<HourUpdate>> FetchHourlyStepsAsync(
            string token, DateTime startUtc, DateTime endUtc, TimeZoneInfo tz, CancellationToken ct)
        {
            var updates = new List<HourUpdate>();
            await ForEachHourlyPointAsync("steps", token, startUtc, endUtc, tz, (hour, point) =>
            {
                if (point.TryGetProperty("steps", out var steps) &&
                    steps.TryGetProperty("countSum", out var sum) &&
                    TryGetNumber(sum, out double count) && count >= 0)
                {
                    long value = (long)count;
                    updates.Add(new HourUpdate(hour, m => m.Steps = value));
                }
            }, ct);
            return updates;
        }

        private async Task<List<HourUpdate>> FetchHourlyCaloriesAsync(
            string token, DateTime startUtc, DateTime endUtc, TimeZoneInfo tz, CancellationToken ct)
        {
            var updates = new List<HourUpdate>();
            await ForEachHourlyPointAsync("total-calories", token, startUtc, endUtc, tz, (hour, point) =>
            {
                if (point.TryGetProperty("totalCalories", out var cals) &&
                    cals.TryGetProperty("kcalSum", out var sum) &&
                    TryGetNumber(sum, out double kcal) && kcal >= 0)
                {
                    updates.Add(new HourUpdate(hour, m => m.Calories = Math.Round(kcal, 1)));
                }
            }, ct);
            return updates;
        }

        private async Task<List<HourUpdate>> FetchHourlyHeartRateAsync(
            string token, DateTime startUtc, DateTime endUtc, TimeZoneInfo tz, CancellationToken ct)
        {
            var updates = new List<HourUpdate>();
            await ForEachHourlyPointAsync("heart-rate", token, startUtc, endUtc, tz, (hour, point) =>
            {
                if (point.TryGetProperty("heartRate", out var hr) &&
                    hr.TryGetProperty("beatsPerMinuteAvg", out var avg) &&
                    TryGetNumber(avg, out double bpm) && bpm > 0)
                {
                    updates.Add(new HourUpdate(hour, m => m.Bpm = Math.Round(bpm, 1)));
                }
            }, ct);
            return updates;
        }

        private async Task<List<HourUpdate>> FetchHourlyZonesAsync(
            string token, DateTime startUtc, DateTime endUtc, TimeZoneInfo tz, CancellationToken ct)
        {
            var updates = new List<HourUpdate>();
            await ForEachHourlyPointAsync("time-in-heart-rate-zone", token, startUtc, endUtc, tz, (hour, point) =>
            {
                ReadZoneMinutes(point, out double z1, out double z2, out double z3, out bool found);
                if (!found) return;

                updates.Add(new HourUpdate(hour, m =>
                {
                    m.Zone1 = Math.Round(z1, 1);
                    m.Zone2 = Math.Round(z2, 1);
                    m.Zone3 = Math.Round(z3, 1);
                }));
            }, ct);
            return updates;
        }


        private static void ReadZoneMinutes(
            JsonElement point, out double z1, out double z2, out double z3, out bool found)
        {
            z1 = z2 = z3 = 0;
            found = false;

            if (!point.TryGetProperty("timeInHeartRateZone", out var tz) ||
                !tz.TryGetProperty("timeInHeartRateZones", out var zones) ||
                zones.ValueKind != JsonValueKind.Array)
                return;

            found = true;
            foreach (var zone in zones.EnumerateArray())
            {
                if (!zone.TryGetProperty("heartRateZone", out var nameEl) ||
                    !zone.TryGetProperty("duration", out var durEl))
                    continue;

                string? durText = durEl.GetString();
                if (string.IsNullOrEmpty(durText) ||
                    !double.TryParse(durText.TrimEnd('s'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double seconds))
                    continue;

                double minutes = seconds / 60.0;
                switch (ZoneIndex(nameEl.GetString()))
                {
                    case 1: z1 += minutes; break;
                    case 2: z2 += minutes; break;
                    case 3: z3 += minutes; break;
                }
            }
        }

        
        private async Task ForEachRollupPointAsync(
            string dataType, Window w, Action<DateOnly, JsonElement> onPoint, CancellationToken ct,
            bool includeTime = true, int chunkDays = int.MaxValue)
        {
            string url = $"{BaseUrl}/{dataType}/dataPoints:dailyRollUp";
            DateOnly chunkStart = w.Start;

            while (chunkStart < w.ExclusiveEnd)
            {
                int span = Math.Min(chunkDays, w.ExclusiveEnd.DayNumber - chunkStart.DayNumber);
                DateOnly chunkEnd = chunkStart.AddDays(span);
                string? pageToken = null;

                for (int page = 0; page < MaxPages; page++)
                {
                    var payload = new Dictionary<string, object?>
                    {
                        ["range"] = new
                        {
                            start = CivilTime(chunkStart, includeTime),
                            end = CivilTime(chunkEnd, includeTime)
                        },
                        ["windowSizeDays"] = 1,
                        ["pageSize"] = span
                    };
                    if (pageToken != null)
                        payload["pageToken"] = pageToken;

                    string body = await SendAsync(
                        HttpMethod.Post, url, w.Token, JsonSerializer.Serialize(payload), dataType, ct);

                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.TryGetProperty("rollupDataPoints", out var points) &&
                        points.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var point in points.EnumerateArray())
                        {
                            if (TryGetCivilDate(point, "civilStartTime", out DateOnly date))
                                onPoint(date, point);
                        }
                    }

                    pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next)
                        ? next.GetString() : null;
                    if (string.IsNullOrEmpty(pageToken)) break;
                }

                chunkStart = chunkEnd;
            }
        }

        
        private async Task ForEachHourlyPointAsync(
            string dataType, string token, DateTime startUtc, DateTime endUtc, TimeZoneInfo tz,
            Action<int, JsonElement> onPoint, CancellationToken ct)
        {
            string url = $"{BaseUrl}/{dataType}/dataPoints:rollUp";
            string? pageToken = null;

            for (int page = 0; page < MaxPages; page++)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["range"] = new { startTime = IsoUtc(startUtc), endTime = IsoUtc(endUtc) },
                    ["windowSize"] = "3600s",
                    ["pageSize"] = 30
                };
                if (pageToken != null)
                    payload["pageToken"] = pageToken;

                string body = await SendAsync(
                    HttpMethod.Post, url, token, JsonSerializer.Serialize(payload), dataType + " (hourly)", ct);

                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("rollupDataPoints", out var points) &&
                    points.ValueKind == JsonValueKind.Array)
                {
                    foreach (var point in points.EnumerateArray())
                    {
                        if (point.TryGetProperty("startTime", out var st) &&
                            DateTimeOffset.TryParse(st.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal, out DateTimeOffset start))
                        {
                            onPoint(TimeZoneInfo.ConvertTime(start, tz).Hour, point);
                        }
                    }
                }

                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next)
                    ? next.GetString() : null;
                if (string.IsNullOrEmpty(pageToken)) break;
            }
        }

        private async Task<string> SendAsync(
            HttpMethod method, string url, string token, string? json, string label, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json != null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                string message = $"Google Health {label} request failed (HTTP {(int)response.StatusCode}).";
                if (DebugIncludeUpstreamBody)
                    message += " " + (body.Length > 300 ? body[..300] : body);

                throw new GoogleHealthApiException((int)response.StatusCode, message);
            }
            return body;
        }

        
        private static async Task<List<T>> RunSafe<T>(
            string name, Func<Task<List<T>>> fetch, ConcurrentBag<string> warnings)
        {
            try
            {
                return await fetch();
            }
            catch (GoogleHealthApiException ex) when (ex.StatusCode is 401 or 403)
            {
                throw;
            }
            catch (Exception ex) when (ex is GoogleHealthApiException or JsonException
                                           or HttpRequestException or InvalidOperationException
                                           or FormatException)
            {
                warnings.Add($"{name}: {ex.Message}");
                return new List<T>();
            }
        }

        private static object CivilTime(DateOnly date, bool includeTime = true) =>
            includeTime
                ? new
                {
                    date = new { year = date.Year, month = date.Month, day = date.Day },
                    time = new { hours = 0, minutes = 0, seconds = 0 }
                }
                : (object)new
                {
                    date = new { year = date.Year, month = date.Month, day = date.Day }
                };

        private static DateTime LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
        {
            DateTime local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(local))
                local = local.AddHours(1); 
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }

        private static string IsoUtc(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        private static bool TryGetCivilDate(JsonElement parent, string property, out DateOnly date)
        {
            date = default;
            if (!parent.TryGetProperty(property, out var civil) ||
                !civil.TryGetProperty("date", out var d) ||
                !d.TryGetProperty("year", out var y) || !y.TryGetInt32(out int year) ||
                !d.TryGetProperty("month", out var mo) || !mo.TryGetInt32(out int month) ||
                !d.TryGetProperty("day", out var da) || !da.TryGetInt32(out int day))
                return false;

            try { date = new DateOnly(year, month, day); return true; }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        private static bool TryGetSleepEndDate(JsonElement sleep, out DateOnly date)
        {
            date = default;
            if (!sleep.TryGetProperty("interval", out var interval))
                return false;

            if (TryGetCivilDate(interval, "civilEndTime", out date))
                return true;

            if (interval.TryGetProperty("endTime", out var endEl) &&
                DateTimeOffset.TryParse(endEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dto))
            {
                date = DateOnly.FromDateTime(dto.DateTime);
                return true;
            }
            return false;
        }

        private static bool TryGetNumber(JsonElement el, out double value)
        {
            value = 0;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.TryGetDouble(out value),
                JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value),
                _ => false
            };
        }
    }
}
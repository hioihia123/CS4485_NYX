using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NyxInsightsWeb.Data;
using NyxInsightsWeb.Models;
using NyxInsightsWeb.Services;

namespace NyxInsightsWeb.Controllers
{
    [Authorize]
    [Route("googlehealth")]
    public class GoogleHealthController : Controller
    {
        private readonly GoogleHealthAuthService _authService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ApplicationDbContext _dbContext;
        private readonly IDataProtector _tokenProtector;
        private readonly IConfiguration _configuration;
        private readonly GoogleHealthDataService _dataService;

        public GoogleHealthController(
            GoogleHealthAuthService authService,
            IHttpClientFactory httpClientFactory,
            ApplicationDbContext dbContext,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration,
            GoogleHealthDataService dataService)
        {
            _authService = authService;
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;
            _configuration = configuration;
            _dataService = dataService; 
            _tokenProtector = dataProtectionProvider.CreateProtector(
                "NyxInsights.GoogleHealthTokens.v1");
        }

        [HttpGet("status")]
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> GetConnectionStatus()
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            var account = await _dbContext.ConnectedHealthAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.UserId == userId);

            if (account == null)
            {
                return Ok(new
                {
                    connected = false,
                    needsReconnect = false,
                    accessTokenExpired = false,
                    canAutoRefresh = false,
                    lastSyncedAt = (DateTime?)null
                });
            }

            bool hasAccessToken = !string.IsNullOrWhiteSpace(account.EncryptedAccessToken);
            bool hasRefreshToken = !string.IsNullOrWhiteSpace(account.EncryptedRefreshToken);
            bool accessTokenExpired = account.AccessTokenExpiresAt.HasValue &&
                account.AccessTokenExpiresAt.Value <= DateTime.UtcNow.AddMinutes(1);
            bool connected = (hasAccessToken && !accessTokenExpired) || hasRefreshToken;

            return Ok(new
            {
                connected,
                needsReconnect = !connected,
                accessTokenExpired,
                canAutoRefresh = hasRefreshToken,
                lastSyncedAt = account.LastSyncedAt
            });
        }

        [HttpGet("connect")]
        public IActionResult Connect()
        {
            string state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            Response.Cookies.Append("GoogleHealthOAuthState", state, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                IsEssential = true,
                Path = "/googlehealth"
            });

            return Redirect(_authService.CreateAuthorizationUrl(state));
        }

        [HttpGet("callback")]
        public async Task<IActionResult> Callback(string? code, string? state, string? error)
        {
            if (!Request.Cookies.TryGetValue("GoogleHealthOAuthState", out string? storedState))
                return BadRequest("OAuth state cookie is missing or expired.");

            Response.Cookies.Delete("GoogleHealthOAuthState", new CookieOptions
            {
                Path = "/googlehealth",
                Secure = true,
                SameSite = SameSiteMode.Lax
            });

            if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(storedState) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(storedState)))
                return BadRequest("Invalid OAuth state.");

            if (!string.IsNullOrEmpty(error))
                return BadRequest("Google authorization was not completed.");

            if (string.IsNullOrWhiteSpace(code))
                return BadRequest("Google did not return an authorization code.");

            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            var token = await _authService.ExchangeCodeAsync(userId, code);
            if (string.IsNullOrWhiteSpace(token.AccessToken))
                return BadRequest("Google did not return an access token.");

            using var identityRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://health.googleapis.com/v4/users/me/identity");
            identityRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token.AccessToken);
            identityRequest.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            var client = _httpClientFactory.CreateClient();
            using var identityResponse = await client.SendAsync(identityRequest);
            string identityBody = await identityResponse.Content.ReadAsStringAsync();

            if (!identityResponse.IsSuccessStatusCode)
            {
                if (identityBody.Contains("ACCOUNT_NOT_LINKED", StringComparison.OrdinalIgnoreCase))
                    return BadRequest("This Google account is not linked to Google Health.");

                return StatusCode((int)identityResponse.StatusCode,
                    "Google Health identity verification failed.");
            }

            string? healthUserId;
            try
            {
                using var document = JsonDocument.Parse(identityBody);
                if (!document.RootElement.TryGetProperty("healthUserId", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                    return BadRequest("Google Health did not return a valid user identity.");

                healthUserId = idElement.GetString();
            }
            catch (JsonException)
            {
                return StatusCode(502, "Google Health returned invalid identity JSON.");
            }

            if (string.IsNullOrWhiteSpace(healthUserId))
                return BadRequest("Google Health returned an empty user identity.");

            var account = await _dbContext.ConnectedHealthAccounts
                .FirstOrDefaultAsync(a => a.UserId == userId);

            string accessToken = _tokenProtector.Protect(token.AccessToken);
            string? refreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
                ? null : _tokenProtector.Protect(token.RefreshToken);
            DateTime expiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresInSeconds ?? 3600);

            if (account == null)
            {
                account = new ConnectedHealthAccount
                {
                    UserId = userId,
                    GoogleHealthUserId = healthUserId,
                    EncryptedAccessToken = accessToken,
                    EncryptedRefreshToken = refreshToken,
                    AccessTokenExpiresAt = expiresAt,
                    GrantedScopes = token.Scope,
                    ConnectedAt = DateTime.UtcNow
                };
                _dbContext.ConnectedHealthAccounts.Add(account);
            }
            else
            {
                bool switchedGoogleAccount = account.GoogleHealthUserId != healthUserId;
                account.GoogleHealthUserId = healthUserId;
                account.EncryptedAccessToken = accessToken;

                if (switchedGoogleAccount)
                    account.EncryptedRefreshToken = refreshToken;
                else if (refreshToken != null)
                    account.EncryptedRefreshToken = refreshToken;

                account.AccessTokenExpiresAt = expiresAt;
                account.GrantedScopes = token.Scope;
                account.ConnectedAt = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync();
            TempData["SuccessMessage"] = "Google Health connected successfully!";
            return RedirectToAction("Index", "Home");
        }

        private async Task<(ConnectedHealthAccount? Account, string? Token, IActionResult? Error)>
            GetValidAccessTokenAsync()
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return (null, null, Unauthorized());

            var account = await _dbContext.ConnectedHealthAccounts
                .FirstOrDefaultAsync(a => a.UserId == userId);

            if (account == null)
                return (null, null, BadRequest("Connect Google Health first."));

            bool tokenIsValid =
                !string.IsNullOrWhiteSpace(account.EncryptedAccessToken) &&
                account.AccessTokenExpiresAt.HasValue &&
                account.AccessTokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(1);

            if (tokenIsValid)
            {
                try
                {
                    return (account,
                        _tokenProtector.Unprotect(account.EncryptedAccessToken), null);
                }
                catch (CryptographicException)
                {
                    return (null, null, StatusCode(500,
                        "The saved Google Health access token cannot be decrypted. Reconnect Google Health."));
                }
            }

            if (string.IsNullOrWhiteSpace(account.EncryptedRefreshToken))
            {
                return (null, null, StatusCode(401,
                    "The Google Health connection needs to be renewed. Reconnect with Google."));
            }

            string refreshToken;
            try
            {
                refreshToken = _tokenProtector.Unprotect(account.EncryptedRefreshToken);
            }
            catch (CryptographicException)
            {
                return (null, null, StatusCode(500,
                    "The saved Google Health refresh token cannot be decrypted. Reconnect Google Health."));
            }

            string? clientId = _configuration["GoogleHealth:ClientId"];
            string? clientSecret = _configuration["GoogleHealth:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret))
            {
                return (null, null, StatusCode(500,
                    "Google Health OAuth configuration is missing on the server."));
            }

            using var refreshBody = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token"
                });

            HttpResponseMessage response;
            try
            {
                response = await _httpClientFactory.CreateClient().PostAsync(
                    "https://oauth2.googleapis.com/token", refreshBody);
            }
            catch (HttpRequestException)
            {
                return (null, null, StatusCode(502,
                    "Could not contact Google to renew the access token. Try again."));
            }
            catch (TaskCanceledException)
            {
                return (null, null, StatusCode(504,
                    "Google token renewal timed out. Try again."));
            }

            using (response)
            {
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    bool invalidGrant = false;
                    try
                    {
                        using var errorDocument = JsonDocument.Parse(responseBody);
                        invalidGrant = errorDocument.RootElement.TryGetProperty(
                            "error", out var errorCode) &&
                            errorCode.GetString() == "invalid_grant";
                    }
                    catch (JsonException)
                    {
                        // Google did not return a parseable error response.
                    }

                    if (invalidGrant)
                    {
                        account.EncryptedRefreshToken = null;
                        await _dbContext.SaveChangesAsync();
                        return (null, null, StatusCode(401,
                            "Google Health authorization has expired or was revoked. Reconnect with Google."));
                    }

                    return (null, null, StatusCode(502,
                        "Google could not renew the access token. Try again or reconnect Google Health."));
                }

                try
                {
                    using var tokenDocument = JsonDocument.Parse(responseBody);
                    var root = tokenDocument.RootElement;

                    if (!root.TryGetProperty("access_token", out var accessTokenValue) ||
                        accessTokenValue.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("expires_in", out var expiresValue) ||
                        !expiresValue.TryGetInt32(out int expiresIn) ||
                        expiresIn <= 0)
                    {
                        return (null, null, StatusCode(502,
                            "Google returned an incomplete token renewal response."));
                    }

                    string? newAccessToken = accessTokenValue.GetString();
                    if (string.IsNullOrWhiteSpace(newAccessToken))
                    {
                        return (null, null, StatusCode(502,
                            "Google returned an empty renewed access token."));
                    }

                    account.EncryptedAccessToken =
                        _tokenProtector.Protect(newAccessToken);
                    account.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);

                    if (root.TryGetProperty("refresh_token", out var newRefreshValue) &&
                        newRefreshValue.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(newRefreshValue.GetString()))
                    {
                        account.EncryptedRefreshToken =
                            _tokenProtector.Protect(newRefreshValue.GetString()!);
                    }

                    await _dbContext.SaveChangesAsync();
                    return (account, newAccessToken, null);
                }
                catch (JsonException)
                {
                    return (null, null, StatusCode(502,
                        "Google returned an invalid token renewal response."));
                }
            }
        }

        private async Task<HttpResponseMessage> SendGoogleRequestAsync(
            HttpMethod method, string url, string accessToken, string? json = null)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (json != null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            return await _httpClientFactory.CreateClient().SendAsync(request);
        }

        private static object BuildDailyRollupPayload(DateOnly startDate, DateOnly exclusiveEnd, int days)
        {
            static object CivilTime(DateOnly date) => new
            {
                date = new { year = date.Year, month = date.Month, day = date.Day },
                time = new { hours = 0, minutes = 0, seconds = 0 }
            };

            return new
            {
                range = new { start = CivilTime(startDate), end = CivilTime(exclusiveEnd) },
                windowSizeDays = 1,
                pageSize = days
            };
        }

        [HttpPost("sync-steps")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncSteps([FromQuery] string? endDate)
        {
            if (!DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var lastDate) || lastDate == DateOnly.MaxValue ||
                lastDate.DayNumber < 29)
                return BadRequest(new { error = "Choose a valid end date (YYYY-MM-DD)." });

            if (lastDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
                return BadRequest(new { error = "The selected date cannot be in the future." });

            const int days = 30;
            DateOnly firstDate = lastDate.AddDays(1 - days);
            DateOnly exclusiveEnd = lastDate.AddDays(1);

            var (account, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            string url = "https://health.googleapis.com/v4/users/me/dataTypes/steps/dataPoints:dailyRollUp";
            string payload = JsonSerializer.Serialize(
                BuildDailyRollupPayload(firstDate, exclusiveEnd, days));

            using var response = await SendGoogleRequestAsync(HttpMethod.Post, url, token!, payload);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode,
                    new { error = "Google Health could not provide step history. Check your permissions or reconnect." });

            var stepsByDate = new Dictionary<string, long>();
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("rollupDataPoints", out var points) &&
                    points.ValueKind == JsonValueKind.Array)
                {
                    foreach (var point in points.EnumerateArray())
                    {
                        if (!point.TryGetProperty("civilStartTime", out var civil) ||
                            !civil.TryGetProperty("date", out var dateValue) ||
                            !point.TryGetProperty("steps", out var steps) ||
                            !steps.TryGetProperty("countSum", out var countValue))
                            continue;

                        int year = dateValue.GetProperty("year").GetInt32();
                        int month = dateValue.GetProperty("month").GetInt32();
                        int day = dateValue.GetProperty("day").GetInt32();
                        string dateKey = $"{year:D4}-{month:D2}-{day:D2}";

                        if (DateOnly.TryParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var parsedDate) &&
                            parsedDate >= firstDate && parsedDate <= lastDate &&
                            long.TryParse(countValue.ToString(), out long count) && count >= 0)
                            stepsByDate[dateKey] = count;
                    }
                }
            }
            catch (JsonException)
            {
                return StatusCode(502, new { error = "Google Health returned invalid step-history data." });
            }
            catch (InvalidOperationException)
            {
                return StatusCode(502, new { error = "Google Health returned an unexpected step-history format." });
            }

            var history = new List<object>();
            var missingDates = new List<string>();
            long totalSteps = 0;
            long? latestSteps = null;
            string? latestDate = null;

            for (int i = 0; i < days; i++)
            {
                string dateKey = firstDate.AddDays(i).ToString("yyyy-MM-dd");
                if (stepsByDate.TryGetValue(dateKey, out long count))
                {
                    history.Add(new { date = dateKey, steps = count });
                    totalSteps += count;
                    latestDate = dateKey;
                    latestSteps = count;
                }
                else
                {
                    missingDates.Add(dateKey);
                }
            }

            account!.LastSyncedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                startDate = firstDate.ToString("yyyy-MM-dd"),
                endDate = lastDate.ToString("yyyy-MM-dd"),
                daysRequested = days,
                daysWithData = history.Count,
                latestDate,
                latestSteps,
                totalSteps,
                history,
                missingDates,
                syncedAt = account.LastSyncedAt
            });
        }

        [HttpGet("test-exercise")]
        public async Task<IActionResult> TestExercise()
        {
            var (account, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            string url = "https://health.googleapis.com/v4/users/me/dataTypes/exercise/dataPoints";
            using var response = await SendGoogleRequestAsync(HttpMethod.Get, url, token!);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, "Google Health exercise request failed.");

            account!.LastSyncedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            return Content(body, "application/json");
        }

        [HttpGet("test-daily-steps")]
        public async Task<IActionResult> TestDailySteps([FromQuery] string date)
        {
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate))
            {
                return BadRequest(new
                {
                    error = "Date must be in yyyy-MM-dd format."
                });
            }

            var tokenResult = await GetValidAccessTokenAsync();

            if (tokenResult.Error != null)
            {
                return tokenResult.Error;
            }

            var accessToken = tokenResult.Token;

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(new
                {
                    error = "Google Health is not connected."
                });
            }

            var startDate = parsedDate;
            var endDate = parsedDate.AddDays(1);

            var payload = BuildDailyRollupPayload(
                startDate,
                endDate,
                1);

            var response = await SendGoogleRequestAsync(
                HttpMethod.Post,
                "https://health.googleapis.com/v4/users/me/dataTypes/steps/dataPoints:dailyRollUp",
                accessToken,
                JsonSerializer.Serialize(payload));

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();

                return StatusCode((int)response.StatusCode, new
                {
                    error = "Google Health steps request failed.",
                    details = errorBody
                });
            }

            var json = await response.Content.ReadAsStringAsync();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            long steps = 0;

            if (root.TryGetProperty("rollupDataPoints", out var rollupDataPoints))
            {
                foreach (var rollupPoint in rollupDataPoints.EnumerateArray())
                {
                    if (rollupPoint.TryGetProperty("steps", out var stepsElement) &&
                        stepsElement.TryGetProperty("countSum", out var countSum))
                    {
                        steps += long.Parse(
                            countSum.GetString() ?? "0",
                            CultureInfo.InvariantCulture);
                    }
                }
            }

            return Ok(new
            {
                date = date,
                steps = steps
            });
        }

        [HttpGet("test-steps-history")]
        public async Task<IActionResult> TestStepsHistory(
            [FromQuery] string? endDate, [FromQuery] int days = 3)
        {
            if (days < 1 || days > 30)
                return BadRequest("Days must be between 1 and 30.");

            if (!DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly selectedEndDate) ||
                selectedEndDate == DateOnly.MaxValue || selectedEndDate.DayNumber < days - 1)
                return BadRequest("Use a valid endDate in YYYY-MM-DD format.");

            DateOnly startDate = selectedEndDate.AddDays(1 - days);
            DateOnly exclusiveEnd = selectedEndDate.AddDays(1);

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            string url = "https://health.googleapis.com/v4/users/me/dataTypes/steps/dataPoints:dailyRollUp";
            string json = JsonSerializer.Serialize(BuildDailyRollupPayload(startDate, exclusiveEnd, days));

            using var response = await SendGoogleRequestAsync(HttpMethod.Post, url, token!, json);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, "Google Health steps history request failed.");

            var stepCounts = new Dictionary<string, long>();
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("rollupDataPoints", out var points) &&
                    points.ValueKind == JsonValueKind.Array)
                {
                    foreach (var point in points.EnumerateArray())
                    {
                        if (!point.TryGetProperty("civilStartTime", out var civil) ||
                            !civil.TryGetProperty("date", out var dateValue) ||
                            !point.TryGetProperty("steps", out var stepsValue) ||
                            !stepsValue.TryGetProperty("countSum", out var countValue))
                            continue;

                        int year = dateValue.GetProperty("year").GetInt32();
                        int month = dateValue.GetProperty("month").GetInt32();
                        int day = dateValue.GetProperty("day").GetInt32();
                        string key = $"{year:D4}-{month:D2}-{day:D2}";

                        if (long.TryParse(countValue.ToString(), out long count) && count >= 0)
                            stepCounts[key] = count;
                    }
                }
            }
            catch (JsonException)
            {
                return StatusCode(502, "Google Health returned invalid steps JSON.");
            }

            var history = new List<object>();
            var missingDates = new List<string>();

            for (int i = 0; i < days; i++)
            {
                string key = startDate.AddDays(i).ToString("yyyy-MM-dd");
                if (stepCounts.TryGetValue(key, out long count))
                    history.Add(new { date = key, steps = count });
                else
                    missingDates.Add(key);
            }

            return Json(new
            {
                startDate = startDate.ToString("yyyy-MM-dd"),
                endDate = selectedEndDate.ToString("yyyy-MM-dd"),
                daysRequested = days,
                history,
                missingDates
            });
        }

        [HttpGet("test-heart-rate")]
        public async Task<IActionResult> TestHeartRate([FromQuery] string? date)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly selectedDate) ||
                selectedDate == DateOnly.MaxValue)
                return BadRequest("Use a valid date in YYYY-MM-DD format.");

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            string startDate = selectedDate.ToString("yyyy-MM-dd");
            string endDate = selectedDate.AddDays(1).ToString("yyyy-MM-dd");

            string filter = $"heart_rate.sample_time.civil_time >= \"{startDate}\" AND " +
                            $"heart_rate.sample_time.civil_time < \"{endDate}\"";

            string url = "https://health.googleapis.com/v4/users/me/dataTypes/heart-rate/dataPoints" +
                        "?pageSize=100&filter=" + Uri.EscapeDataString(filter);

            using var response = await SendGoogleRequestAsync(HttpMethod.Get, url, token!);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return StatusCode(
                    (int)response.StatusCode,
                    "Google Health heart-rate request failed.");

            using JsonDocument doc = JsonDocument.Parse(body);

            double bpmSum = 0;
            int bpmCount = 0;

            if (doc.RootElement.TryGetProperty("dataPoints", out JsonElement dataPoints))
            {
                foreach (JsonElement dataPoint in dataPoints.EnumerateArray())
                {
                    if (!dataPoint.TryGetProperty("heartRate", out JsonElement heartRate))
                        continue;

                    if (!heartRate.TryGetProperty("beatsPerMinute", out JsonElement bpmElement))
                        continue;

                    string? bpmString = bpmElement.GetString();

                    if (double.TryParse(
                            bpmString,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double bpm))
                    {
                        bpmSum += bpm;
                        bpmCount++;
                    }
                }
            }

            double? averageBpm = bpmCount > 0
                ? bpmSum / bpmCount
                : null;

            return Ok(new
            {
                date = startDate,
                bpm = averageBpm,
                samples = bpmCount
            });
        }

        [HttpGet("test-daily-calories")]
        public async Task<IActionResult> TestDailyCalories([FromQuery] string date)
        {
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate))
            {
                return BadRequest(new
                {
                    error = "Date must be in yyyy-MM-dd format."
                });
            }

            var tokenResult = await GetValidAccessTokenAsync();

            if (tokenResult.Error != null)
            {
                return tokenResult.Error;
            }

            var accessToken = tokenResult.Token;

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(new
                {
                    error = "Google Health is not connected."
                });
            }

            var startDate = parsedDate;
            var endDate = parsedDate.AddDays(1);

            var payload = BuildDailyRollupPayload(
                startDate,
                endDate,
                1);

            var response = await SendGoogleRequestAsync(
                HttpMethod.Post,
                "https://health.googleapis.com/v4/users/me/dataTypes/total-calories/dataPoints:dailyRollUp",
                accessToken,
                JsonSerializer.Serialize(payload));

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();

                return StatusCode((int)response.StatusCode, new
                {
                    error = "Google Health calories request failed.",
                    details = errorBody
                });
            }

            var json = await response.Content.ReadAsStringAsync();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            double calories = 0;

            if (root.TryGetProperty("rollupDataPoints", out var rollupDataPoints))
            {
                foreach (var rollupPoint in rollupDataPoints.EnumerateArray())
                {
                    if (rollupPoint.TryGetProperty(
                            "totalCalories",
                            out var totalCalories))
                    {
                        if (totalCalories.TryGetProperty(
                                "kcalSum",
                                out var kcalSum))
                        {
                            calories += kcalSum.GetDouble();
                        }
                    }
                }
            }

            return Ok(new
            {
                date = date,
                calories = calories
            });
        }

        [HttpGet("test-daily-activity")]
        public async Task<IActionResult> TestDailyActivity([FromQuery] string? date)
        {
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly selectedDate))
            {
                return BadRequest("Use a valid date in YYYY-MM-DD format.");
            }

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null)
                return error;

            string url =
                "https://health.googleapis.com/v4/users/me/dataTypes/active-minutes/dataPoints:dailyRollUp";

            var payload = new
            {
                range = new
                {
                    start = new
                    {
                        date = new
                        {
                            year = selectedDate.Year,
                            month = selectedDate.Month,
                            day = selectedDate.Day
                        }
                    },
                    end = new
                    {
                        date = new
                        {
                            year = selectedDate.AddDays(1).Year,
                            month = selectedDate.AddDays(1).Month,
                            day = selectedDate.AddDays(1).Day
                        }
                    }
                },
                windowSizeDays = 1,
                pageSize = 1
            };

            string json = JsonSerializer.Serialize(payload);

            using var response = await SendGoogleRequestAsync(
                HttpMethod.Post,
                url,
                token!,
                json);

            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(
                    (int)response.StatusCode,
                    $"Google Health daily activity request failed: {body}");
            }

            return Content(body, "application/json");
        }

        [HttpGet("test-daily-heart-rate-zones")]
        public async Task<IActionResult> TestDailyHeartRateZones([FromQuery] string date)
        {
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate))
            {
                return BadRequest(new
                {
                    error = "Date must be in yyyy-MM-dd format."
                });
            }

            var tokenResult = await GetValidAccessTokenAsync();

            if (tokenResult.Error != null)
            {
                return tokenResult.Error;
            }

            var accessToken = tokenResult.Token;

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(new
                {
                    error = "Google Health is not connected."
                });
            }

            var startDate = parsedDate;
            var endDate = parsedDate.AddDays(1);

            var payload = BuildDailyRollupPayload(
                startDate,
                endDate,
                1);

            var response = await SendGoogleRequestAsync(
                HttpMethod.Post,
                "https://health.googleapis.com/v4/users/me/dataTypes/time-in-heart-rate-zone/dataPoints:dailyRollUp",
                accessToken,
                JsonSerializer.Serialize(payload));

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();

                return StatusCode((int)response.StatusCode, new
                {
                    error = "Google Health heart-rate-zone request failed.",
                    details = errorBody
                });
            }

            var json = await response.Content.ReadAsStringAsync();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            double lightMinutes = 0;
            double moderateMinutes = 0;
            double vigorousMinutes = 0;

            if (root.TryGetProperty("rollupDataPoints", out var rollupDataPoints))
            {
                foreach (var rollupPoint in rollupDataPoints.EnumerateArray())
                {
                    if (!rollupPoint.TryGetProperty(
                            "timeInHeartRateZone",
                            out var timeInHeartRateZone))
                    {
                        continue;
                    }

                    if (!timeInHeartRateZone.TryGetProperty(
                            "timeInHeartRateZones",
                            out var zones))
                    {
                        continue;
                    }

                    foreach (var zone in zones.EnumerateArray())
                    {
                        var zoneName = zone.GetProperty("heartRateZone").GetString();
                        var duration = zone.GetProperty("duration").GetString();

                        if (string.IsNullOrEmpty(zoneName) ||
                            string.IsNullOrEmpty(duration))
                        {
                            continue;
                        }

                        var secondsString = duration.TrimEnd('s');

                        if (!double.TryParse(
                                secondsString,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var seconds))
                        {
                            continue;
                        }

                        var minutes = seconds / 60.0;

                        switch (zoneName)
                        {
                            case "LIGHT":
                                lightMinutes += minutes;
                                break;

                            case "MODERATE":
                                moderateMinutes += minutes;
                                break;

                            case "VIGOROUS":
                                vigorousMinutes += minutes;
                                break;
                        }
                    }
                }
            }

            return Ok(new
            {
                date = date,
                minutes_in_default_zone_1 = lightMinutes,
                minutes_in_default_zone_2 = moderateMinutes,
                minutes_in_default_zone_3 = vigorousMinutes
            });
        }

        [HttpGet("test-sleep")]
        public async Task<IActionResult> TestSleep([FromQuery] string? date)
        {
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly selectedDate))
            {
                return BadRequest("Use a valid date in YYYY-MM-DD format.");
            }

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null)
                return error;

            DateOnly nextDate = selectedDate.AddDays(1);

            string filter =
                $"sleep.interval.civil_end_time >= \"{selectedDate:yyyy-MM-dd}\" " +
                $"AND sleep.interval.civil_end_time < \"{nextDate:yyyy-MM-dd}\"";

            string url =
                "https://health.googleapis.com/v4/users/me/dataTypes/sleep/dataPoints:reconcile" +
                $"?filter={Uri.EscapeDataString(filter)}&pageSize=25";

            using var response = await SendGoogleRequestAsync(
                HttpMethod.Get,
                url,
                token!);

            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(
                    (int)response.StatusCode,
                    $"Google Health sleep request failed: {body}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);

            double totalMinutesAsleep = 0;
            double totalMinutesInSleepPeriod = 0;
            int sleepRecords = 0;

            if (doc.RootElement.TryGetProperty("dataPoints", out JsonElement dataPoints))
            {
                foreach (JsonElement dataPoint in dataPoints.EnumerateArray())
                {
                    if (!dataPoint.TryGetProperty("sleep", out JsonElement sleep))
                        continue;

                    if (!sleep.TryGetProperty("summary", out JsonElement summary))
                        continue;

                    if (!summary.TryGetProperty(
                            "minutesAsleep",
                            out JsonElement asleepElement))
                        continue;

                    if (!summary.TryGetProperty(
                            "minutesInSleepPeriod",
                            out JsonElement periodElement))
                        continue;

                    if (!double.TryParse(
                            asleepElement.GetString(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double minutesAsleep))
                    {
                        continue;
                    }

                    if (!double.TryParse(
                            periodElement.GetString(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double minutesInSleepPeriod))
                    {
                        continue;
                    }

                    if (minutesInSleepPeriod <= 0)
                        continue;

                    totalMinutesAsleep += minutesAsleep;
                    totalMinutesInSleepPeriod += minutesInSleepPeriod;
                    sleepRecords++;
                }
            }

            double? sleepEfficiency = totalMinutesInSleepPeriod > 0
                ? (totalMinutesAsleep / totalMinutesInSleepPeriod) * 100.0
                : null;

            return Ok(new
            {
                date = selectedDate.ToString("yyyy-MM-dd"),
                sleep_efficiency = sleepEfficiency,
                minutes_asleep = totalMinutesAsleep,
                minutes_in_sleep_period = totalMinutesInSleepPeriod,
                sleep_records = sleepRecords
            });
        }

        
        [HttpGet("test-daily-activity-level")]
        public async Task<IActionResult> TestDailyActivityLevel(
            [FromQuery] string date)
        {
            if (!DateOnly.TryParse(
                    date,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly selectedDate))
            {
                return BadRequest(new
                {
                    error = "Invalid date. Use YYYY-MM-DD."
                });
            }

            var (account, token, error) =
                await GetValidAccessTokenAsync();

            if (error != null)
                return error;

            DateOnly nextDate =
                selectedDate.AddDays(1);

            string filter =
                $"activity_level.interval.civil_start_time >= \"{selectedDate:yyyy-MM-dd}\" " +
                $"AND activity_level.interval.civil_start_time < \"{nextDate:yyyy-MM-dd}\"";

            string url =
                "https://health.googleapis.com/v4/users/me/" +
                "dataTypes/activity-level/dataPoints" +
                $"?filter={Uri.EscapeDataString(filter)}" +
                "&pageSize=10000";

            using var response =
                await SendGoogleRequestAsync(
                    HttpMethod.Get,
                    url,
                    token!);

            string body =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(
                    (int)response.StatusCode,
                    new
                    {
                        error =
                            "Google Health activity-level request failed.",
                        details = body
                    });
            }

            return Content(
                body,
                "application/json");
        }

        [HttpGet("test-daily-active-minutes")]
        public async Task<IActionResult> TestDailyActiveMinutes(
            [FromQuery] string? date)
        {
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly selectedDate))
            {
                return BadRequest("Use a valid date in YYYY-MM-DD format.");
            }

            var (_, token, error) = await GetValidAccessTokenAsync();

            if (error != null)
                return error;

            string url =
                "https://health.googleapis.com/v4/users/me/dataTypes/active-minutes/dataPoints:dailyRollUp";

            var payload = new
            {
                range = new
                {
                    start = new
                    {
                        date = new
                        {
                            year = selectedDate.Year,
                            month = selectedDate.Month,
                            day = selectedDate.Day
                        }
                    },
                    end = new
                    {
                        date = new
                        {
                            year = selectedDate.AddDays(1).Year,
                            month = selectedDate.AddDays(1).Month,
                            day = selectedDate.AddDays(1).Day
                        }
                    }
                },
                windowSizeDays = 1,
                pageSize = 1
            };

            string json = JsonSerializer.Serialize(payload);

            using var response = await SendGoogleRequestAsync(
                HttpMethod.Post,
                url,
                token!,
                json);

            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(
                    (int)response.StatusCode,
                    new
                    {
                        error = "Google Health daily active-minutes request failed.",
                        details = body
                    });
            }

            long lightlyActiveMinutes = 0;
            long moderatelyActiveMinutes = 0;
            long veryActiveMinutes = 0;

            try
            {
                using var document = JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty(
                        "rollupDataPoints",
                        out JsonElement rollupPoints) &&
                    rollupPoints.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement point in rollupPoints.EnumerateArray())
                    {
                        if (!point.TryGetProperty(
                                "activeMinutes",
                                out JsonElement activeMinutes))
                            continue;

                        if (!activeMinutes.TryGetProperty(
                                "activeMinutesRollupByActivityLevel",
                                out JsonElement activityLevels) ||
                            activityLevels.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement activityLevel in activityLevels.EnumerateArray())
                        {
                            if (!activityLevel.TryGetProperty(
                                    "activityLevel",
                                    out JsonElement levelElement))
                                continue;

                            if (!activityLevel.TryGetProperty(
                                    "activeMinutesSum",
                                    out JsonElement minutesElement))
                                continue;

                            string? level = levelElement.GetString();

                            if (!long.TryParse(
                                    minutesElement.ToString(),
                                    out long minutes) ||
                                minutes < 0)
                                continue;

                            switch (level)
                            {
                                case "LIGHT":
                                    lightlyActiveMinutes += minutes;
                                    break;

                                case "MODERATE":
                                    moderatelyActiveMinutes += minutes;
                                    break;

                                case "VIGOROUS":
                                    veryActiveMinutes += minutes;
                                    break;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return StatusCode(
                    502,
                    new
                    {
                        error = "Google Health returned invalid active-minutes JSON."
                    });
            }

            return Ok(new
            {
                date = selectedDate.ToString("yyyy-MM-dd"),
                lightly_active_minutes = lightlyActiveMinutes,
                moderately_active_minutes = moderatelyActiveMinutes,
                very_active_minutes = veryActiveMinutes
            });
        }

        [HttpGet("test-daily-activity-summary")]
        public async Task<IActionResult> TestDailyActivitySummary(
            [FromQuery] string? date)
        {
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly selectedDate))
            {
                return BadRequest("Use a valid date in YYYY-MM-DD format.");
            }

            var (_, token, error) = await GetValidAccessTokenAsync();

            if (error != null)
                return error;



            string activeMinutesUrl =
                "https://health.googleapis.com/v4/users/me/dataTypes/active-minutes/dataPoints:dailyRollUp";

            var activeMinutesPayload = new
            {
                range = new
                {
                    start = new
                    {
                        date = new
                        {
                            year = selectedDate.Year,
                            month = selectedDate.Month,
                            day = selectedDate.Day
                        }
                    },
                    end = new
                    {
                        date = new
                        {
                            year = selectedDate.AddDays(1).Year,
                            month = selectedDate.AddDays(1).Month,
                            day = selectedDate.AddDays(1).Day
                        }
                    }
                },
                windowSizeDays = 1,
                pageSize = 1
            };

            string activeMinutesJson =
                JsonSerializer.Serialize(activeMinutesPayload);

            using var activeMinutesResponse =
                await SendGoogleRequestAsync(
                    HttpMethod.Post,
                    activeMinutesUrl,
                    token!,
                    activeMinutesJson);

            string activeMinutesBody =
                await activeMinutesResponse.Content.ReadAsStringAsync();

            if (!activeMinutesResponse.IsSuccessStatusCode)
            {
                return StatusCode(
                    (int)activeMinutesResponse.StatusCode,
                    new
                    {
                        error =
                            "Google Health active-minutes request failed.",
                        details = activeMinutesBody
                    });
            }

            long lightlyActiveMinutes = 0;
            long moderatelyActiveMinutes = 0;
            long veryActiveMinutes = 0;

            try
            {
                using var document =
                    JsonDocument.Parse(activeMinutesBody);

                if (document.RootElement.TryGetProperty(
                        "rollupDataPoints",
                        out JsonElement rollupPoints) &&
                    rollupPoints.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement point
                        in rollupPoints.EnumerateArray())
                    {
                        if (!point.TryGetProperty(
                                "activeMinutes",
                                out JsonElement activeMinutes))
                            continue;

                        if (!activeMinutes.TryGetProperty(
                                "activeMinutesRollupByActivityLevel",
                                out JsonElement activityLevels) ||
                            activityLevels.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement activityLevel
                            in activityLevels.EnumerateArray())
                        {
                            if (!activityLevel.TryGetProperty(
                                    "activityLevel",
                                    out JsonElement levelElement))
                                continue;

                            if (!activityLevel.TryGetProperty(
                                    "activeMinutesSum",
                                    out JsonElement minutesElement))
                                continue;

                            string? level = levelElement.GetString();

                            if (!long.TryParse(
                                    minutesElement.ToString(),
                                    out long minutes) ||
                                minutes < 0)
                                continue;

                            switch (level)
                            {
                                case "LIGHT":
                                    lightlyActiveMinutes += minutes;
                                    break;

                                case "MODERATE":
                                    moderatelyActiveMinutes += minutes;
                                    break;

                                case "VIGOROUS":
                                    veryActiveMinutes += minutes;
                                    break;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return StatusCode(
                    502,
                    new
                    {
                        error =
                            "Google Health returned invalid active-minutes JSON."
                    });
            }


            DateOnly nextDate =
                selectedDate.AddDays(1);

            string sleepFilter =
                $"sleep.interval.civil_end_time >= \"{selectedDate:yyyy-MM-dd}\" " +
                $"AND sleep.interval.civil_end_time < \"{nextDate:yyyy-MM-dd}\"";

            string sleepUrl =
                "https://health.googleapis.com/v4/users/me/" +
                "dataTypes/sleep/dataPoints:reconcile" +
                $"?filter={Uri.EscapeDataString(sleepFilter)}&pageSize=25";

            using var sleepResponse =
                await SendGoogleRequestAsync(
                    HttpMethod.Get,
                    sleepUrl,
                    token!);

            string sleepBody =
                await sleepResponse.Content.ReadAsStringAsync();

            if (!sleepResponse.IsSuccessStatusCode)
            {
                return StatusCode(
                    (int)sleepResponse.StatusCode,
                    new
                    {
                        error =
                            "Google Health sleep request failed.",
                        details = sleepBody
                    });
            }

            long sleepMinutes = 0;

            try
            {
                using var document =
                    JsonDocument.Parse(sleepBody);

                if (document.RootElement.TryGetProperty(
                        "dataPoints",
                        out JsonElement dataPoints) &&
                    dataPoints.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement dataPoint
                        in dataPoints.EnumerateArray())
                    {
                        if (!dataPoint.TryGetProperty(
                                "sleep",
                                out JsonElement sleep))
                            continue;

                        if (!sleep.TryGetProperty(
                                "summary",
                                out JsonElement summary))
                            continue;

                        if (!summary.TryGetProperty(
                                "minutesAsleep",
                                out JsonElement minutesAsleep))
                            continue;

                        if (long.TryParse(
                                minutesAsleep.ToString(),
                                out long minutes) &&
                            minutes >= 0)
                        {
                            sleepMinutes += minutes;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return StatusCode(
                    502,
                    new
                    {
                        error =
                            "Google Health returned invalid sleep JSON."
                    });
            }


            long sedentaryMinutes =
                Math.Max(
                    0,
                    1440
                    - sleepMinutes
                    - lightlyActiveMinutes
                    - moderatelyActiveMinutes
                    - veryActiveMinutes);

            return Ok(new
            {
                date = selectedDate.ToString("yyyy-MM-dd"),

                sleep_minutes = sleepMinutes,

                lightly_active_minutes =
                    lightlyActiveMinutes,

                moderately_active_minutes =
                    moderatelyActiveMinutes,

                very_active_minutes =
                    veryActiveMinutes,

                sedentary_minutes =
                    sedentaryMinutes
            });
        }

        [HttpGet("test-heart-rate-rollup")]
        public async Task<IActionResult> TestHeartRateRollup([FromQuery] string? date)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly selectedDate))
                return BadRequest("Use a valid date in YYYY-MM-DD format.");

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            string json = JsonSerializer.Serialize(
                BuildDailyRollupPayload(selectedDate, selectedDate.AddDays(1), 1));

            using var response = await SendGoogleRequestAsync(
                HttpMethod.Post,
                "https://health.googleapis.com/v4/users/me/dataTypes/heart-rate/dataPoints:dailyRollUp",
                token!, json);

            string body = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, body);   
        }

        [HttpGet("test-daily-heart-rate-zones-raw")]
        public async Task<IActionResult> TestDailyHeartRateZonesRaw()
        {
            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            using var response = await SendGoogleRequestAsync(
                HttpMethod.Get,
                "https://health.googleapis.com/v4/users/me/dataTypes/daily-heart-rate-zones/dataPoints?pageSize=3",
                token!);

            string body = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, body);   
        }

        [HttpGet("test-model-input")]
        public async Task<IActionResult> TestModelInput(
            [FromQuery] string? date, [FromQuery] string? tz, [FromQuery] bool raw = false,
            CancellationToken ct = default)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var predictionDate))
                return BadRequest("Use a valid date in YYYY-MM-DD format.");

            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(
                    string.IsNullOrWhiteSpace(tz) ? "America/Chicago" : tz);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return BadRequest("Unknown time zone. Use an IANA id such as America/Chicago.");
            }

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            try
            {
                var result = await _dataService.BuildModelRequestAsync(
                    token!, predictionDate, zone, surveyScores: null, historyDays: 30, ct);

                string requestJson = GoogleHealthDataService.SerializeModelRequest(result.Request);

                if (raw)
                    return Content(requestJson, "application/json");

                string warningsJson = JsonSerializer.Serialize(result.Warnings);
                return Content($"{{\"warnings\":{warningsJson},\"request\":{requestJson}}}", "application/json");
            }
            catch (GoogleHealthApiException ex)
            {
                return StatusCode(ex.StatusCode, new { error = ex.Message });
            }
        }

        [HttpGet("test-daily-history")]
        public async Task<IActionResult> TestDailyHistory([FromQuery] string? endDate, CancellationToken ct)
        {
            if (!DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var end))
                return BadRequest("Use a valid endDate in YYYY-MM-DD format.");

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            try
            {
                return Ok(await _dataService.GetDailyHistoryAsync(token!, end, 31, ct));
            }
            catch (GoogleHealthApiException ex)
            {
                return StatusCode(ex.StatusCode, new { error = ex.Message });
            }
        }

    }
}

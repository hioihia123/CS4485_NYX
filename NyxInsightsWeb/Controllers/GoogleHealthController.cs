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

        public GoogleHealthController(
            GoogleHealthAuthService authService,
            IHttpClientFactory httpClientFactory,
            ApplicationDbContext dbContext,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration)
        {
            _authService = authService;
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;
            _configuration = configuration;
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
        public async Task<IActionResult> TestDailySteps([FromQuery] string? date)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly selectedDate) ||
                selectedDate == DateOnly.MaxValue)
                return BadRequest("Use a valid date in YYYY-MM-DD format.");

            var (_, token, error) = await GetValidAccessTokenAsync();
            if (error != null) return error;

            string url = "https://health.googleapis.com/v4/users/me/dataTypes/steps/dataPoints:dailyRollUp";
            string json = JsonSerializer.Serialize(
                BuildDailyRollupPayload(selectedDate, selectedDate.AddDays(1), 1));

            using var response = await SendGoogleRequestAsync(HttpMethod.Post, url, token!, json);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, "Google Health daily steps request failed.");

            return Content(body, "application/json");
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
                return StatusCode((int)response.StatusCode, "Google Health heart-rate request failed.");

            return Content(body, "application/json");
        }
    }
}

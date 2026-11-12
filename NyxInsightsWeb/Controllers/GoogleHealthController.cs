using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NyxInsightsWeb.Services;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NyxInsightsWeb.Data;
using NyxInsightsWeb.Models;

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

        public GoogleHealthController(
            GoogleHealthAuthService authService,
            IHttpClientFactory httpClientFactory,
            ApplicationDbContext dbContext,
            IDataProtectionProvider dataProtectionProvider)
        {
            _authService = authService;
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;

            _tokenProtector = dataProtectionProvider.CreateProtector(
                "NyxInsights.GoogleHealthTokens.v1");
        }

        [HttpGet("connect")]
        public IActionResult Connect()
        {
            // Generate a random value to protect the OAuth flow.
            string state = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(32));

            // Store the state temporarily in a secure cookie.
            Response.Cookies.Append(
                "GoogleHealthOAuthState",
                state,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromMinutes(10),
                    IsEssential = true
                });

            // Redirect the user to Google's authorization page.
            string authorizationUrl =
                _authService.CreateAuthorizationUrl(state);

            return Redirect(authorizationUrl);
        }


        [HttpGet("callback")]
        public async Task<IActionResult> Callback(
            string? code,
            string? state,
            string? error)
        {
            // Handle cases where the user denies authorization.
            if (!string.IsNullOrEmpty(error))
            {
                return BadRequest(
                    "Google Health authorization was not completed: " + error);
            }

            // Retrieve the state value saved when authorization started.
            if (!Request.Cookies.TryGetValue(
                "GoogleHealthOAuthState", out string? storedState))
            {
                return BadRequest("OAuth state cookie is missing.");
            }

            // Delete the temporary cookie.
            Response.Cookies.Delete("GoogleHealthOAuthState");

            // Verify that the returned state matches the original state.
            if (string.IsNullOrEmpty(state) ||
                string.IsNullOrEmpty(storedState) ||
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(state),
                    System.Text.Encoding.UTF8.GetBytes(storedState)))
            {
                return BadRequest("Invalid OAuth state.");
            }

            if (string.IsNullOrEmpty(code))
            {
                return BadRequest("Google did not return an authorization code.");
            }

            // Get the ID of the currently logged-in Nyx user.
            string? userId =
                User.FindFirst(
                    System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            // Exchange the authorization code for Google Health tokens.
            var token = await _authService.ExchangeCodeAsync(userId, code);

            // Create a request to retrieve the Google Health identity.
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://health.googleapis.com/v4/users/me/identity");

            // Authenticate the request using the access token.
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token.AccessToken);

            // Send the request to Google Health.
            var httpClient = _httpClientFactory.CreateClient();

            using var response = await httpClient.SendAsync(request);

            string responseBody =
                await response.Content.ReadAsStringAsync();

            // Check whether the Google account is linked to Google Health.
            if (!response.IsSuccessStatusCode)
            {
                if (responseBody.Contains(
                    "ACCOUNT_NOT_LINKED",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(
                        "Your Google account is not linked to Google Health. " +
                        "Open the Google Health mobile app, sign in, and complete account linking before trying again.");
                }

                return Content(
                    $"Google Health identity verification failed.\n" +
                    $"HTTP Status: {(int)response.StatusCode}\n" +
                    $"Google Response: {responseBody}");
            }

            // Read the Google Health identity.
            using var identityDocument = JsonDocument.Parse(responseBody);

            if (!identityDocument.RootElement.TryGetProperty(
                "healthUserId", out var healthUserIdElement))
            {
                return BadRequest(
                    "Google Health did not return a valid user identity.");
            }

            string? healthUserId = healthUserIdElement.GetString();

            if (string.IsNullOrWhiteSpace(healthUserId))
            {
                return BadRequest(
                    "Google Health returned an empty user identity.");
            }

            // Find an existing Google Health connection for this Nyx user.
            var connectedAccount = await _dbContext.ConnectedHealthAccounts
                .FirstOrDefaultAsync(account => account.UserId == userId);

            // Encrypt the access token before storing it.
            string encryptedAccessToken =
                _tokenProtector.Protect(token.AccessToken);

            // Encrypt the refresh token if Google returned one.
            string? encryptedRefreshToken = null;

            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                encryptedRefreshToken =
                    _tokenProtector.Protect(token.RefreshToken);
            }

            // Calculate when the access token expires.
            DateTime accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(
                token.ExpiresInSeconds ?? 3600);

            // Create a new connection if one doesn't already exist.
            if (connectedAccount == null)
            {
                connectedAccount = new ConnectedHealthAccount
                {
                    UserId = userId,
                    GoogleHealthUserId = healthUserId,
                    EncryptedAccessToken = encryptedAccessToken,
                    EncryptedRefreshToken = encryptedRefreshToken,
                    AccessTokenExpiresAt = accessTokenExpiresAt,
                    GrantedScopes = token.Scope,
                    ConnectedAt = DateTime.UtcNow
                };

                _dbContext.ConnectedHealthAccounts.Add(connectedAccount);
            }
            else
            {
                // Update an existing connection.
                connectedAccount.GoogleHealthUserId = healthUserId;
                connectedAccount.EncryptedAccessToken = encryptedAccessToken;

                // Preserve an existing refresh token if Google doesn't return a new one.
                if (encryptedRefreshToken != null)
                {
                    connectedAccount.EncryptedRefreshToken =
                        encryptedRefreshToken;
                }

                connectedAccount.AccessTokenExpiresAt = accessTokenExpiresAt;
                connectedAccount.GrantedScopes = token.Scope;
                connectedAccount.ConnectedAt = DateTime.UtcNow;
            }

            // Save the connection to PostgreSQL.
            await _dbContext.SaveChangesAsync();

            return Content(
                "Your Google Health account has been successfully connected to Nyx Insights.");
        }
    }
}

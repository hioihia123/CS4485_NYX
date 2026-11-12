
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace NyxInsightsWeb.Services
{
    public class GoogleHealthAuthService
    {
        private readonly IConfiguration _configuration;

        private const string RedirectUri =
            "https://localhost:7135/googlehealth/callback";

        private readonly string[] _scopes =
        {
            "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly",
            "https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly",
            "https://www.googleapis.com/auth/googlehealth.sleep.readonly"
        };

        public GoogleHealthAuthService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

      
        private GoogleAuthorizationCodeFlow CreateFlow()
        {
            string? clientId =
                _configuration["GoogleHealth:ClientId"];

            string? clientSecret =
                _configuration["GoogleHealth:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "Google Health OAuth credentials are not configured.");
            }

            return new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = clientId,
                        ClientSecret = clientSecret
                    },
                    Scopes = _scopes,

                    // Request a refresh token for future API access.
                    Prompt = "consent"
                });
}


        public string CreateAuthorizationUrl(string state)
        {
            var flow = CreateFlow();

            var request =
            flow.CreateAuthorizationCodeRequest(RedirectUri);

            request.State = state;

            return request.Build().ToString();
        }

        public async Task<TokenResponse> ExchangeCodeAsync(
            string userId,
            string authorizationCode)
        {
            var flow = CreateFlow();

            return await flow.ExchangeCodeForTokenAsync(
                userId,
                authorizationCode,
                RedirectUri,
                CancellationToken.None);
        }
    }
}

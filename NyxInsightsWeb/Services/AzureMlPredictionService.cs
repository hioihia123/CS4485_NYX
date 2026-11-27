
using System.Text;
using System.Text.Json;

namespace NyxInsightsWeb.Services
{
    public class AzureMlPredictionService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        public AzureMlPredictionService(
            HttpClient httpClient,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        public async Task<JsonDocument> PredictAsync(
            Dictionary<string, object?> features)
        {
            // New FastAPI prediction service URL
            string? scoringUri =
                _configuration["NyxPredictor:ScoringUri"];

            // Private key shared with the FastAPI service
            string? apiKey =
                _configuration["NyxPredictor:ApiKey"];

            if (string.IsNullOrWhiteSpace(scoringUri))
            {
                throw new InvalidOperationException(
                    "NYX prediction API URL is not configured.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "NYX prediction API key is not configured.");
            }

            // Same input schema
            var requestBody = new
            {
                input_data = features
            };

            string json = JsonSerializer.Serialize(requestBody);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                scoringUri);

            // FastAPI uses X-Nyx-Api-Key,
            // not Azure ML Bearer authentication.
            request.Headers.Add("X-Nyx-Api-Key", apiKey);

            request.Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await _httpClient.SendAsync(request);

            string responseBody =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"NYX prediction API returned HTTP " +
                    $"{(int)response.StatusCode}.");
            }

            return JsonDocument.Parse(responseBody);
        }
    }
}


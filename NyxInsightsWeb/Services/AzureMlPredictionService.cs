using System.Net.Http.Headers;
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
            string? scoringUri =
                _configuration["AzureML:ScoringUri"];

            string? endpointKey =
                _configuration["AzureML:EndpointKey"];

            if (string.IsNullOrWhiteSpace(scoringUri))
            {
                throw new InvalidOperationException(
                    "Azure ML scoring URL is not configured.");
            }

            if (string.IsNullOrWhiteSpace(endpointKey))
            {
                throw new InvalidOperationException(
                    "Azure ML endpoint key is not configured.");
            }

            var requestBody = new
            {
                input_data = features
            };

            string json = JsonSerializer.Serialize(requestBody);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                scoringUri);

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    endpointKey);

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
                    $"Azure ML returned HTTP " +
                    $"{(int)response.StatusCode}: {responseBody}");
            }

            return JsonDocument.Parse(responseBody);
        }
    }
}

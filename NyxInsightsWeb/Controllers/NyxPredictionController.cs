#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NyxInsightsWeb.Controllers;

// A logged-in user can request predictions without ever seeing the Azure ML key.
[Authorize]
[ApiController]
[Route("api/nyx/predict")]
public sealed class NyxPredictionController : ControllerBase
{
    private static readonly HashSet<string> RequiredFeatures = new(StringComparer.Ordinal)
    {
        "bpm",
        "mindfulness_session",
        "exertion_points_percentage",
        "very_active_minutes",
        "moderately_active_minutes",
        "lightly_active_minutes",
        "sedentary_minutes",
        "minutes_in_default_zone_1",
        "minutes_in_default_zone_2",
        "minutes_in_default_zone_3",
        "steps",
        "calories"
    };

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<NyxPredictionController> _logger;

    public NyxPredictionController(
        IConfiguration configuration,
        IHttpClientFactory clientFactory,
        ILogger<NyxPredictionController> logger)
    {
        _configuration = configuration;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Predict(
        [FromBody] NyxPredictionRequest? input,
        CancellationToken cancellationToken)
    {
        if (input?.Features is null ||
            input.Features.Count != RequiredFeatures.Count ||
            !RequiredFeatures.SetEquals(input.Features.Keys) ||
            input.Features.Values.Any(value => !double.IsFinite(value)))
        {
            return BadRequest(new
            {
                error = "Provide all 12 numeric NYX features using their exact field names."
            });
        }

        var scoringUri = _configuration["AzureML:ScoringUri"];
        var apiKey = _configuration["AzureML:ApiKey"];

        if (string.IsNullOrWhiteSpace(scoringUri) || string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("AzureML scoring URI or API key is not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "The prediction service has not been configured."
            });
        }

        if (!Uri.TryCreate(scoringUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogError("AzureML scoring URI is not a valid HTTPS address.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "The prediction service address is invalid."
            });
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            // The deployed score.py reads json.loads(raw_data)["input_data"].
            Content = JsonContent.Create(new { input_data = input.Features })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            var client = _clientFactory.CreateClient("NyxAzureMl");
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Do not return internal Azure errors or authentication details to browsers.
                _logger.LogWarning("Azure ML scoring failed with HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "Azure ML could not process this prediction. Check the server logs."
                });
            }

            await using var resultStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var result = await JsonDocument.ParseAsync(
                resultStream, cancellationToken: cancellationToken);

            return Ok(result.RootElement.Clone());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = "The AI model did not respond in time. Please try again."
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the Azure ML endpoint.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Could not connect to the prediction service."
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Azure ML returned invalid JSON.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "The prediction service returned an unreadable response."
            });
        }
    }
}

public sealed class NyxPredictionRequest
{
    public Dictionary<string, double>? Features { get; set; }
}


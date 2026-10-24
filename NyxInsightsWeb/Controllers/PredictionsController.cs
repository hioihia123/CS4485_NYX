
using Microsoft.AspNetCore.Mvc;
using NyxInsightsWeb.Services;

namespace NyxInsightsWeb.Controllers
{
    [ApiController]
    [Route("api/predictions")]
    public class PredictionsController : ControllerBase
    {
        private readonly AzureMlPredictionService _predictionService;
        private readonly FoundryAgentService _foundryAgentService;
        private readonly ILogger<PredictionsController> _logger;

        public PredictionsController(
            AzureMlPredictionService predictionService,
            FoundryAgentService foundryAgentService,
            ILogger<PredictionsController> logger)
        {
            _predictionService = predictionService;
            _foundryAgentService = foundryAgentService;
            _logger = logger;
        }

        [HttpPost("predict")]
        public async Task<IActionResult> Predict(
            [FromBody] Dictionary<string, object?> features)
        {
            string[] requiredFeatures =
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

            var missingFeatures = requiredFeatures
                .Where(feature =>
                    !features.TryGetValue(feature, out var value)
                    || value == null)
                .ToArray();

            if (missingFeatures.Length > 0)
            {
                return BadRequest(new
                {
                    error = "Some required Fitbit features are missing.",
                    missingFeatures
                });
            }

            try
            {
                using var result =
                    await _predictionService.PredictAsync(features);

                return Content(
                    result.RootElement.GetRawText(),
                    "application/json");
            }
            catch (HttpRequestException)
            {
                return StatusCode(502, new
                {
                    error = "The Azure ML prediction service failed."
                });
            }
            catch (InvalidOperationException)
            {
                return StatusCode(500, new
                {
                    error = "The prediction service is not configured."
                });
            }
        }

        [HttpPost("predict-and-explain")]
        public async Task<IActionResult> PredictAndExplain(
            [FromBody] Dictionary<string, object?> features)
        {
            string[] requiredFeatures =
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

            var missingFeatures = requiredFeatures
                .Where(feature =>
                    !features.TryGetValue(feature, out var value)
                    || value == null)
                .ToArray();

            if (missingFeatures.Length > 0)
            {
                return BadRequest(new
                {
                    error = "Some required Fitbit features are missing.",
                    missingFeatures
                });
            }

            try
            {
                // Step 1: Get predictions from Azure ML.
                using var result =
                    await _predictionService.PredictAsync(features);

                string predictionJson =
                    result.RootElement.GetRawText();

                // Step 2: Ask the Foundry agent to explain the results.
                string prompt = $"""
                    Explain these Nyx Insights stress and sleep predictions
                    for a user viewing a wellness dashboard.

                    The following JSON is the actual output from our
                    Azure ML prediction service. Use it as the source
                    of truth for all prediction values and model details.

                    {predictionJson}

                    Explain the predicted stress score and next-night
                    sleep efficiency in clear, supportive language.

                    If feature contributions are present, explain them
                    as model-estimated contributions, not causes.
                    Mention relevant model performance limitations
                    and uncertainty. Do not invent measurements,
                    claim a diagnosis, or imply that correlation
                    proves causation.

                    Keep the explanation concise and practical.
                    Return only the explanation text, not JSON.
                    """;

                string explanation =
                    await _foundryAgentService.GetExplanationAsync(prompt);

                // Step 3: Return the original predictions and explanation.
                return Ok(new
                {
                    predictionResult = result.RootElement.Clone(),
                    explanation
                });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "An upstream service failed during prediction and explanation.");

                return StatusCode(502, new
                {
                    error = "Azure ML or Foundry could not complete the request."
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "A prediction or Foundry service setting is missing.");

                return StatusCode(500, new
                {
                    error = "A prediction or Foundry service setting is not configured."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Prediction and explanation failed.");

                return StatusCode(500, new
                {
                    error = "An unexpected error occurred while generating the prediction explanation."
                });
            }
        }
    }
}

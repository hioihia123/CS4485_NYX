using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using NyxInsightsWeb.Data;
using NyxInsightsWeb.Models;
using NyxInsightsWeb.Services;

using System.Text.Json;

namespace NyxInsightsWeb.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/predictions")]
    public class PredictionsController : ControllerBase
    {
        private readonly AzureMlPredictionService _predictionService;
        private readonly FoundryAgentService _foundryAgentService;
        private readonly ILogger<PredictionsController> _logger;
        private readonly ApplicationDbContext _db;
        private readonly UserManager<IdentityUser> _userManager;

        public PredictionsController(
            AzureMlPredictionService predictionService,
            FoundryAgentService foundryAgentService,
            ILogger<PredictionsController> logger,
            ApplicationDbContext db,
            UserManager<IdentityUser> userManager)
        {
            _predictionService = predictionService;
            _foundryAgentService = foundryAgentService;
            _logger = logger;
            _db = db;
            _userManager = userManager;
        }


        // AZURE ML ONLY
        // This endpoint does not save anything.

        [HttpPost("predict")]
        public async Task<IActionResult> Predict(
            [FromBody] Dictionary<string, object?> features)
        {
            IActionResult? validation =
                ValidateFeatures(features);

            if (validation != null)
            {
                return validation;
            }

            try
            {
                using JsonDocument result =
                    await _predictionService.PredictAsync(features);

                return Content(
                    result.RootElement.GetRawText(),
                    "application/json");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Azure ML prediction failed.");

                return StatusCode(502, new
                {
                    error =
                        "The Azure ML prediction service failed."
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "Azure ML configuration is missing.");

                return StatusCode(500, new
                {
                    error =
                        "The Azure ML prediction service is not configured."
                });
            }
        }


        // Frontend
        //     ↓
        // Azure ML
        //     ↓
        // Stress + sleep prediction
        //     ↓
        // Foundry
        //     ↓
        // PostgreSQL
        //     ↓
        // Frontend

        [HttpPost("predict-and-explain")]
        public async Task<IActionResult> PredictAndExplain(
            [FromBody] Dictionary<string, object?> features)
        {
            IActionResult? validation =
                ValidateFeatures(features);

            if (validation != null)
            {
                return validation;
            }

            try
            {
                // Get prediction from Azure Machine Learning.

                using JsonDocument result =
                    await _predictionService.PredictAsync(features);

                JsonElement root =
                    result.RootElement;

                string predictionJson =
                    root.GetRawText();



                if (!root.TryGetProperty(
                        "predictions",
                        out JsonElement predictions))
                {
                    throw new InvalidOperationException(
                        "Azure ML response did not contain predictions.");
                }


                if (!predictions.TryGetProperty(
                        "stress_score",
                        out JsonElement stressElement))
                {
                    throw new InvalidOperationException(
                        "Azure ML response did not contain stress_score.");
                }


                if (!predictions.TryGetProperty(
                        "sleep_efficiency_next_night",
                        out JsonElement sleepElement))
                {
                    throw new InvalidOperationException(
                        "Azure ML response did not contain " +
                        "sleep_efficiency_next_night.");
                }


                double stressScore =
                    stressElement.GetDouble();

                double sleepScore =
                    sleepElement.GetDouble();


                // Ask Azure Foundry for explanation.
                // If Foundry fails, prediction still succeeds and
                // still gets saved.
                // -------------------------------------------------

                string prompt = $"""
                    You are the Nyx Insights wellness dashboard assistant.

                    Explain this Azure Machine Learning prediction
                    in simple, concise language.

                    Prediction JSON:

                    {predictionJson}

                    Explain:
                    - predicted stress score
                    - predicted next-night sleep efficiency
                    - at most three important feature contributions

                    Rules:
                    - Do not diagnose medical conditions.
                    - Do not invent measurements.
                    - Do not claim causation.
                    - Use the Azure ML JSON as the source of truth.
                    - State that the results are experimental predictions.
                    - Keep the response concise.
                    """;


                string explanation;

                try
                {
                    explanation =
                        await _foundryAgentService
                            .GetExplanationAsync(prompt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Foundry explanation failed.");

                    explanation =
                        "AI explanation is currently unavailable. " +
                        "The stress and sleep predictions were " +
                        "generated successfully.";
                }


                // Identify current logged-in user.

                string? userId =
                    _userManager.GetUserId(User);

                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized(new
                    {
                        error =
                            "You must be logged in to save a prediction."
                    });
                }


                // Build database row.

                var record =
                    new HealthPrediction
                    {
                        UserId =
                            userId,

                        CreatedAt =
                            DateTime.UtcNow,

                        Bpm =
                            GetDouble(
                                features,
                                "bpm"),

                        MindfulnessSession =
                            GetDouble(
                                features,
                                "mindfulness_session"),

                        ExertionPointsPercentage =
                            GetDouble(
                                features,
                                "exertion_points_percentage"),

                        VeryActiveMinutes =
                            GetDouble(
                                features,
                                "very_active_minutes"),

                        ModeratelyActiveMinutes =
                            GetDouble(
                                features,
                                "moderately_active_minutes"),

                        LightlyActiveMinutes =
                            GetDouble(
                                features,
                                "lightly_active_minutes"),

                        SedentaryMinutes =
                            GetDouble(
                                features,
                                "sedentary_minutes"),

                        MinutesInDefaultZone1 =
                            GetDouble(
                                features,
                                "minutes_in_default_zone_1"),

                        MinutesInDefaultZone2 =
                            GetDouble(
                                features,
                                "minutes_in_default_zone_2"),

                        MinutesInDefaultZone3 =
                            GetDouble(
                                features,
                                "minutes_in_default_zone_3"),

                        Steps =
                            GetDouble(
                                features,
                                "steps"),

                        Calories =
                            GetDouble(
                                features,
                                "calories"),

                        StressScore =
                            stressScore,

                        SleepEfficiencyNextNight =
                            sleepScore,

                        AiExplanation =
                            explanation
                    };


                // Save to PostgreSQL.

                _db.HealthPredictions.Add(record);

                await _db.SaveChangesAsync();


                _logger.LogInformation(
                    "Saved HealthPrediction ID {PredictionId} " +
                    "for user {UserId}.",
                    record.Id,
                    userId);


                // Return everything to browser.

                return Ok(new
                {
                    predictionId =
                        record.Id,

                    predictionResult =
                        root.Clone(),

                    explanation
                });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Azure ML request failed.");

                return StatusCode(502, new
                {
                    error =
                        "Azure ML could not complete the prediction."
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "Prediction processing failed.");

                return StatusCode(500, new
                {
                    error = ex.Message
                });
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(
                    ex,
                    "Database save failed.");

                return StatusCode(500, new
                {
                    error =
                        "The prediction was generated, but saving " +
                        "it to PostgreSQL failed."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected prediction failure.");

                return StatusCode(500, new
                {
                    error =
                        "An unexpected error occurred while " +
                        "processing the prediction."
                });
            }
        }


        // user history
        // GET /api/predictions/history

        [HttpGet("history")]
        public async Task<IActionResult> GetHistory()
        {
            string? userId =
                _userManager.GetUserId(User);

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized(new
                {
                    error =
                        "You must be logged in."
                });
            }


            var history =
                await _db.HealthPredictions

                    .Where(x =>
                        x.UserId == userId)

                    .OrderByDescending(x =>
                        x.CreatedAt)

                    .Select(x => new
                    {
                        x.Id,

                        x.CreatedAt,

                        x.Bpm,

                        x.MindfulnessSession,

                        x.ExertionPointsPercentage,

                        x.VeryActiveMinutes,

                        x.ModeratelyActiveMinutes,

                        x.LightlyActiveMinutes,

                        x.SedentaryMinutes,

                        x.MinutesInDefaultZone1,

                        x.MinutesInDefaultZone2,

                        x.MinutesInDefaultZone3,

                        x.Steps,

                        x.Calories,

                        x.StressScore,

                        x.SleepEfficiencyNextNight,

                        x.AiExplanation
                    })

                    .ToListAsync();


            return Ok(history);
        }


        // required feature validation

        private IActionResult? ValidateFeatures(
            Dictionary<string, object?> features)
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


            string[] missingFeatures =
                requiredFeatures

                    .Where(feature =>
                        !features.TryGetValue(
                            feature,
                            out object? value)

                        || value == null)

                    .ToArray();


            if (missingFeatures.Length > 0)
            {
                return BadRequest(new
                {
                    error =
                        "Some required health features are missing.",

                    missingFeatures
                });
            }


            return null;
        }


        // json number conversion
        // Dictionary<string, object?> values normally arrive
        // as JsonElement objects.

        private static double GetDouble(
            Dictionary<string, object?> features,
            string key)
        {
            if (!features.TryGetValue(
                    key,
                    out object? value)

                || value == null)
            {
                throw new ArgumentException(
                    $"Missing feature: {key}");
            }


            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind ==
                    JsonValueKind.Number)
                {
                    return jsonElement.GetDouble();
                }


                if (jsonElement.ValueKind ==
                        JsonValueKind.String

                    && double.TryParse(
                        jsonElement.GetString(),
                        out double parsed))
                {
                    return parsed;
                }
            }


            return Convert.ToDouble(value);
        }
    }
}

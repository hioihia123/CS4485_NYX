
using Microsoft.AspNetCore.Mvc;
using NyxInsightsWeb.Services;

namespace NyxInsightsWeb.Controllers
{
    [ApiController]
    [Route("api/foundry")]
    public class FoundryController : ControllerBase
    {
        private readonly FoundryAgentService _agentService;
        private readonly ILogger<FoundryController> _logger;

        public FoundryController(
            FoundryAgentService agentService,
            ILogger<FoundryController> logger)
        {
            _agentService = agentService;
            _logger = logger;
        }

        [HttpPost("test")]
        public async Task<IActionResult> TestAgent(
            [FromBody] AgentTestRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return BadRequest(new
                {
                    error = "A prompt is required."
                });
            }

            try
            {
                string explanation =
                    await _agentService.GetExplanationAsync(request.Prompt);

                return Ok(new { explanation });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Foundry agent test failed.");

                return StatusCode(500, new
                {
                    error = "The Foundry agent call failed. Check the application terminal logs."
                });
            }
        }
    }

    public class AgentTestRequest
    {
        public string Prompt { get; set; } = string.Empty;
    }
}


using Azure.AI.Projects;
using Azure.AI.Extensions.OpenAI;
using Azure.Identity;

#pragma warning disable OPENAI001

namespace NyxInsightsWeb.Services
{
    public class FoundryAgentService
    {
        private readonly IConfiguration _configuration;

        public FoundryAgentService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<string> GetExplanationAsync(string prompt)
        {
            string? projectEndpoint =
                _configuration["Foundry:ChatCompletionsUrl"];

            string? agentName =
                _configuration["Foundry:AgentName"];

            if (string.IsNullOrWhiteSpace(projectEndpoint))
            {
                throw new InvalidOperationException(
                    "Foundry project endpoint is not configured.");
            }

            if (string.IsNullOrWhiteSpace(agentName))
            {
                throw new InvalidOperationException(
                    "Foundry agent name is not configured.");
            }

            var projectClient = new AIProjectClient(
                endpoint: new Uri(projectEndpoint),
                tokenProvider: new DefaultAzureCredential());

            var responsesClient =
                projectClient.ProjectOpenAIClient
                    .GetProjectResponsesClientForAgent(agentName);

            var result = await responsesClient.CreateResponseAsync(prompt);

            return result.Value.GetOutputText();
        }
    }
}

#pragma warning restore OPENAI001

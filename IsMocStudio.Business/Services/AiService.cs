using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IsMocStudio.Business.Services
{
    public class AiService : IAiService
    {
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public AiService(IConfiguration configuration, HttpClient httpClient)
        {
            _configuration = configuration;
            _httpClient = httpClient;
        }

        public async Task<string> GenerateJsonBriefAsync(string projectType, string description)
        {
            var apiKey = _configuration["OpenAI:ApiKey"];

            if (string.IsNullOrEmpty(apiKey) || apiKey == "TA_CLE_API_OPENAI_ICI")
            {
                return GenerateFallbackBrief(projectType, description);
            }

            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                // Prompt construit par concaténation simple pour éviter tout bug de compilation C#
                var prompt = "Tu es un Designer UI/UX expert et un Architecte Front-end. " +
                             "Un client souhaite créer une application de type '" + projectType + "' avec la description suivante : \"" + description + "\". " +
                             "Analyse ce besoin et retourne EXCLUSIVEMENT un objet JSON valide (sans markdown, sans blocs de code) avec la structure exacte suivante : " +
                             "{\"AppName\": \"Nom de l'app\", \"Theme\": \"Light\", \"PrimaryColor\": \"#10B981\", \"SecondaryColor\": \"#3B82F6\", " +
                             "\"Screens\": [{\"ScreenName\": \"Accueil\", \"LayoutType\": \"Grid\", \"Components\": [\"Barre de recherche\", \"Liste\"]}], " +
                             "\"CoreFeatures\": [\"Fonctionnalité 1\"], \"AiGeneratedSummary\": \"Résumé en français.\"}";

                var requestBody = new
                {
                    model = "gpt-4o-mini",
                    messages = new[]
                    {
                        new { role = "system", content = "Tu es un assistant technique expert en UI/UX qui retourne uniquement du JSON structuré." },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.7
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    return GenerateFallbackBrief(projectType, description);
                }

                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var aiMessage = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                aiMessage = aiMessage?.Replace("```json", "").Replace("```", "").Trim();
                return aiMessage ?? GenerateFallbackBrief(projectType, description);
            }
            catch
            {
                return GenerateFallbackBrief(projectType, description);
            }
        }

        private string GenerateFallbackBrief(string projectType, string description)
        {
            var briefObject = new
            {
                AppName = "Votre projet",
                Theme = "Light",
                PrimaryColor = "#F59E0B",
                SecondaryColor = "#0F172A",
                Screens = new[]
                {
                    new { ScreenName = "Accueil", LayoutType = "Dashboard", Components = new[] { "Présentation du projet", "Fonctionnalités principales", "Appel à l'action" } },
                    new { ScreenName = "Découvrir", LayoutType = "Grid", Components = new[] { "Liste des contenus ou services", "Filtres", "Recherche" } },
                    new { ScreenName = "Contact", LayoutType = "Form", Components = new[] { "Coordonnées", "Formulaire de contact" } }
                },
                CoreFeatures = new[] { "Navigation responsive", "Présentation des offres ou contenus", "Formulaire de contact" },
                AiGeneratedSummary = "Structure de départ pour " + projectType + " basée sur : '" + description + "'"
            };

            return JsonSerializer.Serialize(briefObject, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Logging;

namespace IsMocStudio.Business.Services
{
    /// <summary>Erreur d'un fournisseur IA, renvoyée telle quelle (code, type, message) au client.</summary>
    public class AiProviderException : Exception
    {
        public AiProviderException(string provider, int statusCode, string? errorType, string message, Exception? inner = null)
            : base(message, inner)
        {
            Provider = provider;
            StatusCode = statusCode;
            ErrorType = errorType;
        }

        public string Provider { get; }

        /// <summary>Code HTTP renvoyé (0 = réseau injoignable, 408 = délai dépassé, 413 = tronqué, 422 = refus du modèle).</summary>
        public int StatusCode { get; }
        public string? ErrorType { get; }

        /// <summary>Surcharge, limite de débit, panne serveur ou délai : une nouvelle tentative peut réussir.</summary>
        public bool IsTransient => StatusCode is 0 or 408 or 429 or 500 or 502 or 503 or 504 or 529;
    }

    /// <summary>
    /// Réponse coupée par la limite de tokens (stop_reason « max_tokens »). Conserve le texte partiel
    /// pour que l'appelant tente une auto-réparation de la maquette avant de renvoyer l'erreur.
    /// </summary>
    public sealed class AiTruncatedResponseException : AiProviderException
    {
        public AiTruncatedResponseException(string provider, string partialText)
            : base(provider, 413, "max_tokens", $"La réponse de {provider} a été tronquée (maquette trop volumineuse).")
        {
            PartialText = partialText;
        }

        public string PartialText { get; }
    }

    /// <summary>
    /// Client HTTP brut de l'API Messages d'Anthropic (https://api.anthropic.com/v1/messages), en STREAMING (SSE) :
    /// les réponses très longues arrivent au fil de l'eau, sans limite des requêtes non streamées ni coupure HTTP.
    /// Requêtes standard uniquement (aucune option bêta). Client typé (Program.cs).
    /// </summary>
    public sealed class AnthropicMessagesClient
    {
        public const string ApiVersion = "2023-06-01";

        /// <summary>Durée maximale d'une génération streamée (lecture complète du flux).</summary>
        public static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(12);

        private const int MaxAttempts = 2;
        private const int MaxLoggedBodyLength = 20_000;

        /// <summary>
        /// Modèles acceptant les sorties structurées (output_config.format). Pour les autres (ex. claude-sonnet-4-6),
        /// le JSON est obtenu par le prompt puis validé/réparé côté serveur.
        /// </summary>
        private static readonly HashSet<string> StructuredOutputModels = new(StringComparer.Ordinal)
        {
            "claude-sonnet-5-5", "claude-sonnet-5", "claude-opus-5-5", "claude-opus-5", "claude-opus-4-8",
            "claude-haiku-5-5", "claude-haiku-4-5", "claude-fable-5", "claude-fable-5-1", "claude-fable-5-2"
        };

        private readonly HttpClient _httpClient;
        private readonly ILogger<AnthropicMessagesClient> _logger;

        public AnthropicMessagesClient(HttpClient httpClient, ILogger<AnthropicMessagesClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClient.BaseAddress ??= new Uri("https://api.anthropic.com/");
        }

        /// <summary>
        /// Envoie un message en streaming et retourne le texte complet reconstitué.
        /// Lève <see cref="AiTruncatedResponseException"/> (avec le texte partiel) si max_tokens est atteint.
        /// </summary>
        public async Task<string> CreateMessageAsync(
            string apiKey,
            string model,
            int maxTokens,
            string systemPrompt,
            string userPrompt,
            ReferenceImage? image,
            JsonObject? jsonSchema,
            CancellationToken cancellationToken = default)
        {
            var payload = BuildRequestBody(model, maxTokens, systemPrompt, userPrompt, image, jsonSchema).ToJsonString();

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await StreamOnceAsync(apiKey, model, maxTokens, payload, cancellationToken);
                }
                catch (AiProviderException exception) when (exception is not AiTruncatedResponseException
                    && exception.StatusCode is 429 or 500 or 502 or 503 or 529 && attempt < MaxAttempts)
                {
                    // Surcharge ou erreur serveur : une seconde tentative après une courte pause.
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                }
            }
        }

        private static JsonObject BuildRequestBody(string model, int maxTokens, string systemPrompt, string userPrompt, ReferenceImage? image, JsonObject? jsonSchema)
        {
            var content = new JsonArray();
            if (image is not null)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = image.MediaType,
                        ["data"] = Convert.ToBase64String(image.Data)
                    }
                });
            }
            content.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt });

            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = maxTokens,
                ["stream"] = true,
                // Prompt système identique d'un appel à l'autre : mis en cache (fonction standard, sans en-tête bêta).
                ["system"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = systemPrompt,
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                    }
                },
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } }
            };

            if (jsonSchema is not null && StructuredOutputModels.Contains(model))
            {
                // Copie : un nœud JSON ne peut appartenir qu'à un seul document (le schéma peut être réutilisé).
                body["output_config"] = new JsonObject
                {
                    ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = jsonSchema.DeepClone() }
                };
            }

            return body;
        }

        private async Task<string> StreamOnceAsync(string apiKey, string model, int maxTokens, string payload, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            // Le Timeout du HttpClient couvre l'envoi et la réception des en-têtes ; la lecture du flux est bornée ici.
            using var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            streamTimeout.CancelAfter(StreamTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, streamTimeout.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Anthropic {Model} : aucune réponse avant le délai de {Timeout}.", model, _httpClient.Timeout);
                throw new AiProviderException("Anthropic", 408, "timeout", "Anthropic n'a pas répondu dans le délai autorisé.", exception);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogError(exception, "Anthropic {Model} : API injoignable.", model);
                throw new AiProviderException("Anthropic", 0, "network", "Impossible de joindre l'API Anthropic.", exception);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                    // Code HTTP exact + corps complet d'erreur (ne contient jamais la clé API) pour le diagnostic.
                    _logger.LogError(
                        "Anthropic {Model} a répondu HTTP {StatusCode} ({Reason}). request-id={RequestId}. Corps de l'erreur : {ErrorBody}",
                        model,
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        RequestId(response),
                        raw.Length > MaxLoggedBodyLength ? raw[..MaxLoggedBodyLength] + "…" : raw);
                    throw ToProviderException(response.StatusCode, raw);
                }

                try
                {
                    return await ReadEventStreamAsync(response, model, maxTokens, streamTimeout.Token);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(exception, "Anthropic {Model} : flux interrompu après {Timeout}.", model, StreamTimeout);
                    throw new AiProviderException("Anthropic", 408, "timeout", $"La génération a dépassé {StreamTimeout.TotalMinutes:0} minutes.", exception);
                }
                catch (IOException exception)
                {
                    _logger.LogError(exception, "Anthropic {Model} : connexion coupée pendant le streaming.", model);
                    throw new AiProviderException("Anthropic", 0, "network", "La connexion avec Anthropic a été coupée pendant la génération.", exception);
                }
            }
        }

        /// <summary>
        /// Lit le flux SSE : texte reconstitué depuis les « content_block_delta » (text_delta),
        /// stop_reason final depuis « message_delta », erreurs depuis les événements « error ».
        /// </summary>
        private async Task<string> ReadEventStreamAsync(HttpResponseMessage response, string model, int maxTokens, CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var text = new StringBuilder();
            var data = new StringBuilder();
            string? stopReason = null;
            string? refusalCategory = null;
            var outputTokens = 0;
            var completed = false;

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart());
                    continue;
                }

                // Ligne vide = fin d'un événement SSE.
                if (line.Length != 0 || data.Length == 0) continue;

                using (var document = JsonDocument.Parse(data.ToString()))
                {
                    data.Clear();
                    var root = document.RootElement;
                    switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
                    {
                        case "content_block_delta":
                            if (root.TryGetProperty("delta", out var delta)
                                && delta.TryGetProperty("type", out var deltaType) && deltaType.GetString() == "text_delta"
                                && delta.TryGetProperty("text", out var deltaText))
                            {
                                text.Append(deltaText.GetString());
                            }
                            break;

                        case "message_delta":
                            if (root.TryGetProperty("delta", out var messageDelta))
                            {
                                if (messageDelta.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String) stopReason = stop.GetString();
                                if (messageDelta.TryGetProperty("stop_details", out var details) && details.ValueKind == JsonValueKind.Object
                                    && details.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String)
                                {
                                    refusalCategory = category.GetString();
                                }
                            }
                            if (root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("output_tokens", out var tokens) && tokens.TryGetInt32(out var count)) outputTokens = count;
                            break;

                        case "message_stop":
                            completed = true;
                            break;

                        case "error":
                            var hasError = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object;
                            var errorType = hasError && error.TryGetProperty("type", out var et) ? et.GetString() : null;
                            var errorMessage = hasError && error.TryGetProperty("message", out var em) ? em.GetString() : null;
                            _logger.LogError("Anthropic {Model} : erreur dans le flux ({Type}) après {Length} caractères : {Message}", model, errorType, text.Length, errorMessage);
                            throw new AiProviderException("Anthropic", errorType == "overloaded_error" ? 529 : 500, errorType, errorMessage ?? "Erreur Anthropic pendant le streaming.");
                    }
                }

                if (completed) break;
            }

            _logger.LogInformation("Anthropic {Model} : flux terminé, stop_reason={StopReason}, {OutputTokens}/{MaxTokens} tokens de sortie, {Length} caractères.",
                model, stopReason, outputTokens, maxTokens, text.Length);

            // Toujours vérifier stop_reason avant d'utiliser le contenu.
            if (stopReason == "refusal")
            {
                _logger.LogWarning("Anthropic {Model} a refusé la demande (catégorie {Category}).", model, refusalCategory ?? "inconnue");
                throw new AiProviderException("Anthropic", 422, "refusal", "Le modèle a refusé de traiter cette demande. Reformulez votre idée.");
            }

            if (stopReason == "max_tokens")
            {
                _logger.LogWarning("Anthropic {Model} : réponse tronquée à {MaxTokens} tokens ({Length} caractères reçus).", model, maxTokens, text.Length);
                throw new AiTruncatedResponseException("Anthropic", text.ToString());
            }

            if (!completed && stopReason is null)
            {
                _logger.LogError("Anthropic {Model} : flux terminé sans message_stop ({Length} caractères reçus).", model, text.Length);
                throw new AiTruncatedResponseException("Anthropic", text.ToString());
            }

            if (text.Length == 0)
            {
                _logger.LogError("Anthropic {Model} : réponse sans texte. stop_reason={StopReason}.", model, stopReason);
                throw new AiProviderException("Anthropic", 204, "empty", "Claude a retourné une réponse vide.");
            }

            return text.ToString();
        }

        private static string RequestId(HttpResponseMessage response) =>
            response.Headers.TryGetValues("request-id", out var ids) ? string.Join(",", ids) : "-";

        private static AiProviderException ToProviderException(HttpStatusCode status, string raw)
        {
            string? errorType = null;
            var message = $"Erreur Anthropic (HTTP {(int)status}).";
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    errorType = error.TryGetProperty("type", out var type) ? type.GetString() : null;
                    if (error.TryGetProperty("message", out var detail) && !string.IsNullOrWhiteSpace(detail.GetString())) message = detail.GetString()!;
                }
            }
            catch (JsonException)
            {
                // Corps non JSON (proxy, passerelle) : message générique conservé.
            }

            return new AiProviderException("Anthropic", (int)status, errorType, message);
        }
    }
}

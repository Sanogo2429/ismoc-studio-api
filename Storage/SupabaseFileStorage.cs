using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.api.Storage
{
    /// <summary>Réglages « Supabase:* » (variables Render Supabase__Url, Supabase__ServiceKey, Supabase__Bucket…).</summary>
    public sealed record SupabaseStorageOptions(Uri Url, string ServiceKey, string Bucket)
    {
        public const string DefaultBucket = "ismoc-attachments";

        /// <summary>Null si Supabase n'est pas configuré (URL https + clé serveur obligatoires).</summary>
        public static SupabaseStorageOptions? FromConfiguration(IConfiguration configuration)
        {
            var url = configuration["Supabase:Url"]?.Trim().TrimEnd('/');
            var key = configuration["Supabase:ServiceKey"]?.Trim();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Supabase:Url doit être une adresse https (ex. https://<projet>.supabase.co).");
            var bucket = configuration["Supabase:Bucket"]?.Trim();
            return new SupabaseStorageOptions(uri, key, string.IsNullOrWhiteSpace(bucket) ? DefaultBucket : bucket);
        }
    }

    /// <summary>
    /// Supabase Storage (API REST) : bucket PRIVÉ, accès uniquement par URL signée à durée limitée.
    /// La clé serveur (service role / secret) reste côté API : elle n'est jamais transmise au navigateur.
    /// </summary>
    public sealed class SupabaseFileStorage : IFileStorage
    {
        public const string HttpClientName = "supabase-storage";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SupabaseStorageOptions _options;
        private readonly ILogger<SupabaseFileStorage> _logger;

        public SupabaseFileStorage(IHttpClientFactory httpClientFactory, SupabaseStorageOptions options, ILogger<SupabaseFileStorage> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options;
            _logger = logger;
        }

        public string Name => $"Supabase Storage (bucket privé « {_options.Bucket} »)";

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var request = NewRequest(HttpMethod.Post, $"object/{_options.Bucket}/{EncodeKey(key)}");
            request.Content = new StreamContent(content);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            request.Headers.Add("x-upsert", "false");
            request.Headers.CacheControl = new CacheControlHeaderValue { Private = true, MaxAge = TimeSpan.FromHours(1) };
            await SendAsync(request, "envoi", key, cancellationToken);
        }

        public async Task<SignedFileLink> CreateSignedLinkAsync(string key, string contentType, TimeSpan lifetime, string? downloadName, CancellationToken cancellationToken = default)
        {
            var seconds = (int)Math.Clamp(lifetime.TotalSeconds, 30, 3600);
            using var request = NewRequest(HttpMethod.Post, $"object/sign/{_options.Bucket}/{EncodeKey(key)}");
            request.Content = JsonContent.Create(new { expiresIn = seconds });
            using var response = await SendAsync(request, "signature", key, cancellationToken);
            var signed = await response.Content.ReadFromJsonAsync<SignResponse>(cancellationToken);
            if (string.IsNullOrWhiteSpace(signed?.SignedUrl))
                throw new InvalidOperationException("Supabase Storage n'a pas renvoyé de lien signé.");

            // signedURL est relatif à /storage/v1 (ex. « /object/sign/bucket/cle?token=… »).
            var url = signed.SignedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? signed.SignedUrl
                : $"{_options.Url.AbsoluteUri.TrimEnd('/')}/storage/v1{(signed.SignedUrl.StartsWith('/') ? "" : "/")}{signed.SignedUrl}";
            if (downloadName is not null) url += $"&download={Uri.EscapeDataString(downloadName)}";
            return new SignedFileLink(url, DateTimeOffset.UtcNow.AddSeconds(seconds));
        }

        public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            try
            {
                using var request = NewRequest(HttpMethod.Delete, $"object/{_options.Bucket}/{EncodeKey(key)}");
                using var response = await Client().SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                    _logger.LogWarning("Suppression Supabase de {Key} refusée ({Status}).", key, (int)response.StatusCode);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(exception, "Suppression Supabase de {Key} impossible.", key);
            }
        }

        /// <summary>
        /// Au démarrage : crée le bucket s'il n'existe pas (privé, 10 Mo max par fichier) et refuse un bucket public.
        /// </summary>
        public async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
        {
            using (var request = NewRequest(HttpMethod.Get, $"bucket/{_options.Bucket}"))
            using (var response = await Client().SendAsync(request, cancellationToken))
            {
                if (response.IsSuccessStatusCode)
                {
                    var bucket = await response.Content.ReadFromJsonAsync<BucketResponse>(cancellationToken);
                    if (bucket?.Public == true)
                        throw new InvalidOperationException($"Le bucket Supabase « {_options.Bucket} » est public : passez-le en privé (Storage → bucket → Edit → Public off).");
                    return;
                }
            }

            using var create = NewRequest(HttpMethod.Post, "bucket");
            create.Content = JsonContent.Create(new { id = _options.Bucket, name = _options.Bucket, @public = false, file_size_limit = 10 * 1024 * 1024 });
            using var created = await SendAsync(create, "création du bucket", _options.Bucket, cancellationToken);
            _logger.LogInformation("Bucket Supabase privé « {Bucket} » créé.", _options.Bucket);
        }

        private HttpClient Client() => _httpClientFactory.CreateClient(HttpClientName);

        private HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, new Uri(_options.Url, $"/storage/v1/{path}"));
            // Clé serveur : en-tête apikey (passerelle Supabase) + Authorization (droits « service role » sur le bucket privé).
            request.Headers.Add("apikey", _options.ServiceKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceKey);
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, string key, CancellationToken cancellationToken)
        {
            var response = await Client().SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return response;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            _logger.LogError("Supabase Storage : {Operation} de {Key} refusée ({Status}) : {Body}", operation, key, (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
            throw new InvalidOperationException($"Stockage des fichiers indisponible ({operation}).");
        }

        /// <summary>Encode chaque segment de la clé (les « / » restent des séparateurs).</summary>
        private static string EncodeKey(string key) => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

        private sealed class SignResponse
        {
            [JsonPropertyName("signedURL")]
            public string? SignedUrl { get; set; }
        }

        private sealed class BucketResponse
        {
            [JsonPropertyName("public")]
            public bool Public { get; set; }
        }
    }
}

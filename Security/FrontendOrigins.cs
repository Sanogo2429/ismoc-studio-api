namespace IsMocStudio.api.Security
{
    /// <summary>
    /// Adresses du frontend autorisées : appels CORS et retour après la connexion Google.
    /// Site web (Vite dev 5173 / preview 4173), application mobile Capacitor (Android : https://localhost,
    /// iOS : capacitor://localhost), origines de production « Cors:AllowedOrigins » et, en développement,
    /// toute URL localtunnel (https://*.loca.lt) dont l'adresse change à chaque relance du tunnel.
    /// </summary>
    public sealed class FrontendOrigins
    {
        public const string LocalFrontend = "http://localhost:5173";

        private readonly string[] _allowedOrigins;
        private readonly bool _allowLocalTunnels;

        /// <summary>Site utilisé quand aucune origine valide n'est connue (« Frontend:BaseUrl », sinon localhost:5173).</summary>
        public string DefaultFrontend { get; }

        public FrontendOrigins(IConfiguration configuration, IHostEnvironment environment)
        {
            var extraOrigins = configuration.GetSection("Cors:AllowedOrigins").GetChildren()
                .Select(origin => origin.Value?.Trim().TrimEnd('/'))
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Cast<string>();
            var configuredFrontend = configuration["Frontend:BaseUrl"]?.Trim().TrimEnd('/');
            DefaultFrontend = string.IsNullOrWhiteSpace(configuredFrontend) ? LocalFrontend : configuredFrontend;
            _allowedOrigins = [LocalFrontend, DefaultFrontend, "http://localhost:4173", "http://127.0.0.1:4173", "https://localhost", "capacitor://localhost", .. extraOrigins];
            _allowLocalTunnels = environment.IsDevelopment();
        }

        /// <summary>
        /// Origine du site qui lance la connexion : paramètre returnUrl, sinon en-têtes Origin / Referer du navigateur.
        /// </summary>
        public string ResolveFromRequest(string? returnUrl, HttpRequest request)
        {
            foreach (var candidate in new[] { returnUrl, request.Headers.Origin.ToString(), OriginOf(request.Headers.Referer.ToString()) })
            {
                var origin = OriginOf(candidate);
                if (IsAllowed(origin)) return origin!;
            }

            return DefaultFrontend;
        }

        private static string? OriginOf(string? url) =>
            Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "capacitor"
                ? uri.GetLeftPart(UriPartial.Authority)
                : null;

        public bool IsAllowed(string? origin)
        {
            if (string.IsNullOrWhiteSpace(origin)) return false;
            if (_allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;

            // Tunnels de développement : localtunnel (*.loca.lt) et Cloudflare Quick Tunnel (*.trycloudflare.com).
            return _allowLocalTunnels
                && Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                && originUri.Scheme == Uri.UriSchemeHttps
                && (originUri.Host.EndsWith(".loca.lt", StringComparison.OrdinalIgnoreCase)
                    || originUri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Origine demandée si elle est autorisée, sinon le frontend par défaut.</summary>
        public string Resolve(string? requestedOrigin)
        {
            var origin = requestedOrigin?.Trim().TrimEnd('/');
            return IsAllowed(origin) ? origin! : DefaultFrontend;
        }
    }
}

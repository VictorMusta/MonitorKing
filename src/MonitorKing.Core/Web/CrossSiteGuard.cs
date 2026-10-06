using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MonitorKing.Core.Web;

/// <summary>
/// Protège les routes qui modifient un état contre les requêtes venues d'un autre site (CSRF).
/// N'importe quelle page ouverte dans le navigateur peut envoyer un POST « simple » (formulaire, fetch sans
/// pré-contrôle) vers http://localhost ou vers le serveur, et le navigateur y joint le mot de passe mémorisé.
/// Elle ne choisit pas, en revanche, les en-têtes Origin et Sec-Fetch-Site : c'est le navigateur qui les écrit.
/// </summary>
public static class CrossSiteGuard
{
    /// <summary>Refuse (403) les écritures venues d'un autre site sur toutes les routes du groupe. Les lectures passent.</summary>
    public static TBuilder RefuseCrossSiteWrites<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            return Allows(request.Method, request.Host.Value, request.Headers.Origin, request.Headers["Sec-Fetch-Site"])
                ? await next(context)
                : Results.Text("Requête refusée : elle ne vient pas de ce tableau de bord.", statusCode: StatusCodes.Status403Forbidden);
        });

    /// <summary>
    /// Vrai si la requête peut passer. Sans Origin ni Sec-Fetch-Site, ce n'est pas un navigateur (agent, curl) :
    /// il n'a pas d'identifiants mémorisés qu'une page pourrait lui faire utiliser.
    /// </summary>
    /// <param name="host">En-tête Host de la requête, port compris.</param>
    public static bool Allows(string method, string? host, string? origin, string? fetchSite)
    {
        // Les lectures restent ouvertes : sans en-tête CORS, la page d'un autre site ne peut pas lire la réponse.
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return true;

        // « none » : requête lancée par l'utilisateur lui-même, pas par une page. « same-site » est refusé aussi :
        // un autre port de localhost est un autre programme.
        if (!string.IsNullOrEmpty(fetchSite) && fetchSite is not ("same-origin" or "none")) return false;

        if (string.IsNullOrEmpty(origin)) return true;

        // Hôte et port seulement, pas le schéma : derrière Caddy, le serveur reçoit en HTTP une page servie en HTTPS.
        // « Origin: null » (iframe isolée, fichier local) n'est pas une adresse : refusé.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase);
    }
}

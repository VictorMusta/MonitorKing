using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MonitorKing.Core.Web;

namespace MonitorKing.Core.Tests;

public class CrossSiteGuardTests
{
    private const string Host = "localhost:5757";

    [Theory]
    [InlineData("https://exemple.org", null)]                         // autre site
    [InlineData("http://localhost:3000", null)]                       // autre port de localhost
    [InlineData("http://127.0.0.1:5757", null)]                       // autre nom de la même machine
    [InlineData("http://localhost", null)]                            // port absent
    [InlineData("null", null)]                                        // iframe isolée, fichier local
    [InlineData("localhost:5757", null)]                              // pas une origine
    [InlineData("http://localhost:5757,https://exemple.org", null)]   // en-tête en double
    [InlineData(null, "cross-site")]
    [InlineData(null, "same-site")]
    [InlineData(null, "autre-chose")]
    [InlineData("http://localhost:5757", "cross-site")]               // un seul signal contraire suffit
    [InlineData("https://exemple.org", "same-origin")]
    public void Refuse_une_ecriture_venue_d_ailleurs(string? origin, string? fetchSite)
    {
        Assert.False(CrossSiteGuard.Allows("POST", Host, origin, fetchSite));
    }

    [Theory]
    [InlineData(null, null)]                                          // agent, curl : pas un navigateur
    [InlineData("http://localhost:5757", null)]
    [InlineData("http://localhost:5757", "same-origin")]
    [InlineData("HTTP://LOCALHOST:5757", "same-origin")]
    [InlineData(null, "same-origin")]
    [InlineData(null, "none")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Accepte_une_ecriture_du_dashboard_ou_d_un_outil_sans_navigateur(string? origin, string? fetchSite)
    {
        Assert.True(CrossSiteGuard.Allows("POST", Host, origin, fetchSite));
    }

    [Theory]
    [InlineData("monitorking.exemple.org", "https://monitorking.exemple.org", true)]        // derrière Caddy : reçu en HTTP, page en HTTPS
    [InlineData("monitorking.exemple.org", "https://monitorking.exemple.org:8443", false)]
    [InlineData("monitorking.exemple.org", "https://autre.exemple.org", false)]
    [InlineData("monitorking.exemple.org", "https://monitorking.exemple.org.pirate.example", false)]
    [InlineData("MonitorKing.Exemple.org", "https://monitorking.exemple.org", true)]
    [InlineData("127.0.0.1:5757", "http://127.0.0.1:5757", true)]
    [InlineData("[::1]:5757", "http://[::1]:5757", true)]
    [InlineData("", "https://exemple.org", false)]
    [InlineData(null, "https://exemple.org", false)]
    public void Compare_l_hote_et_le_port_sans_le_schema(string? host, string origin, bool allowed)
    {
        Assert.Equal(allowed, CrossSiteGuard.Allows("POST", host, origin, null));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void Laisse_passer_les_lectures_meme_d_un_autre_site(string method)
    {
        Assert.True(CrossSiteGuard.Allows(method, Host, "https://exemple.org", "cross-site"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Filtre_toutes_les_methodes_qui_ecrivent(string method)
    {
        Assert.False(CrossSiteGuard.Allows(method, Host, "https://exemple.org", null));
        Assert.True(CrossSiteGuard.Allows(method, Host, "http://localhost:5757", "same-origin"));
    }

    [Fact]
    public async Task Le_filtre_du_groupe_repond_403_sans_executer_la_route()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var writes = 0;
        var api = app.MapGroup("/api").RefuseCrossSiteWrites();
        api.MapPost("/ecrire", () =>
        {
            writes++;
            return Results.NoContent();
        });
        api.MapGet("/lire", () => "ok");
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var self = client.BaseAddress.GetLeftPart(UriPartial.Authority);

        Assert.Equal(HttpStatusCode.Forbidden, await Send(client, HttpMethod.Post, "/api/ecrire", origin: "https://exemple.org"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(client, HttpMethod.Post, "/api/ecrire", fetchSite: "cross-site"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(client, HttpMethod.Post, "/api/ecrire", origin: "https://exemple.org", host: "monitorking.exemple.org"));
        Assert.Equal(0, writes);

        Assert.Equal(HttpStatusCode.NoContent, await Send(client, HttpMethod.Post, "/api/ecrire", origin: self, fetchSite: "same-origin"));
        Assert.Equal(HttpStatusCode.NoContent, await Send(client, HttpMethod.Post, "/api/ecrire", origin: "https://monitorking.exemple.org", host: "monitorking.exemple.org"));
        Assert.Equal(HttpStatusCode.NoContent, await Send(client, HttpMethod.Post, "/api/ecrire"));
        Assert.Equal(3, writes);

        Assert.Equal(HttpStatusCode.OK, await Send(client, HttpMethod.Get, "/api/lire", origin: "https://exemple.org", fetchSite: "cross-site"));
    }

    private static async Task<HttpStatusCode> Send(HttpClient client, HttpMethod method, string path, string? origin = null, string? fetchSite = null, string? host = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (host is not null) request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}

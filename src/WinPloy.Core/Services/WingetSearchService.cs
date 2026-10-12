using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Services;

/// <summary>Application trouvée dans le dépôt communautaire WinGet.</summary>
public sealed record WingetSearchHit(string Id, string Name, string Publisher, string Version, string Description)
{
    /// <summary>Deuxième ligne de la liste de résultats.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string> { Id };
            if (Publisher.Length > 0)
            {
                parts.Add(Publisher);
            }
            if (Version.Length > 0)
            {
                parts.Add($"v{Version}");
            }
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>Échec de recherche déjà traduit pour l'utilisateur.</summary>
public sealed class WingetSearchException(string message) : Exception(message)
{
}

/// <summary>
/// Recherche d'un identifiant WinGet sans winget installé sur le poste d'administration :
/// l'index public du dépôt communautaire (microsoft/winget-pkgs) est interrogé en HTTPS.
/// Si la requête échoue (pas d'accès Internet, proxy, service indisponible), la fenêtre
/// propose le site winget.run et la saisie manuelle de l'identifiant.
/// </summary>
public sealed class WingetSearchService
{
    /// <summary>Site public listant les identifiants WinGet, à ouvrir ou à copier.</summary>
    public const string BrowseUrl = "https://winget.run/";

    public const int MinQueryLength = 2;

    private const string SearchEndpoint = "https://api.winstall.app/apps/search?limit=25&q=";

    private static readonly HttpClient Client = CreateClient();

    public async Task<IReadOnlyList<WingetSearchHit>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var text = (query ?? "").Trim();
        if (text.Length < MinQueryLength)
        {
            throw new WingetSearchException(string.Format(Strings.WingetQueryTooShort, MinQueryLength));
        }

        string json;
        try
        {
            using var response = await Client.GetAsync(SearchEndpoint + Uri.EscapeDataString(text), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new WingetSearchException(string.Format(Strings.WingetHttpStatus, (int)response.StatusCode, response.ReasonPhrase));
            }
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new WingetSearchException(Strings.WingetTimeout);
        }
        catch (HttpRequestException ex)
        {
            throw new WingetSearchException(string.Format(Strings.WingetUnreachable, ex.Message));
        }

        try
        {
            return Parse(json);
        }
        catch (JsonException)
        {
            throw new WingetSearchException(Strings.WingetBadResponse);
        }
    }

    /// <summary>Lit la réponse JSON : { "data": [ { "_id", "name", "publisher", "desc", "latestVersion" } ] }.</summary>
    internal static IReadOnlyList<WingetSearchHit> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hits = new List<WingetSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Text(item, "_id");
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            var name = Text(item, "name");
            hits.Add(new WingetSearchHit(
                id,
                name.Length == 0 ? id : name,
                Text(item, "publisher"),
                Text(item, "latestVersion"),
                Text(item, "desc")));
        }
        return hits;
    }

    private static string Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinPloy", "2.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

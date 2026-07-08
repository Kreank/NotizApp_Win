using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotizApp.Services;

/// <summary>Ein Treffer aus der Gerätewissen-Wissensbasis (Artikel/Ersatzteil/Wartung).</summary>
public record WissenTreffer
{
    [JsonPropertyName("artikelnummer")] public string Artikelnummer { get; init; } = "";
    [JsonPropertyName("bezeichnung")] public string Bezeichnung { get; init; } = "";
    [JsonPropertyName("kurztext1")] public string? Kurztext1 { get; init; }
    [JsonPropertyName("kurztext2")] public string? Kurztext2 { get; init; }
    [JsonPropertyName("langtext")] public string? Langtext { get; init; }
    [JsonPropertyName("einheit")] public string? Einheit { get; init; }
    [JsonPropertyName("preis")] public double? Preis { get; init; }
    [JsonPropertyName("katalog_art")] public string? KatalogArt { get; init; }
    [JsonPropertyName("katalog_titel")] public string? KatalogTitel { get; init; }
    [JsonPropertyName("hersteller")] public string? Hersteller { get; init; }
    [JsonPropertyName("stand")] public string? Stand { get; init; }
    [JsonPropertyName("ean")] public string? Ean { get; init; }
    [JsonPropertyName("warengruppe_name")] public string? WarengruppeName { get; init; }
}

/// <summary>Antwort-Hülle der Such-API.</summary>
file record WissenAntwort
{
    [JsonPropertyName("treffer")] public List<WissenTreffer> Treffer { get; init; } = new();
    [JsonPropertyName("anzahl")] public int Anzahl { get; init; }
}

/// <summary>
/// Fragt die Gerätewissen-Wissensbasis über deren REST-Endpunkt ab
/// (Header „X-Api-Key"). URL und Key kommen aus den Settings und bleiben lokal.
/// </summary>
public class GeraeteWissenService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly string _endpunkt;
    readonly string _key;

    public GeraeteWissenService(string endpunkt, string apiKey)
    {
        _endpunkt = (endpunkt ?? "").Trim();
        _key = (apiKey ?? "").Trim();
    }

    /// <summary>Ist ein Endpunkt und ein API-Key hinterlegt?</summary>
    public bool Konfiguriert =>
        !string.IsNullOrWhiteSpace(_endpunkt) && !string.IsNullOrWhiteSpace(_key);

    /// <summary>Volltextsuche in der Wissensbasis. Wirft bei Netzwerk-/HTTP-Fehlern.</summary>
    public async Task<List<WissenTreffer>> SucheAsync(string frage, CancellationToken ct)
    {
        var trenner = _endpunkt.Contains('?') ? '&' : '?';
        var url = $"{_endpunkt}{trenner}q={Uri.EscapeDataString(frage.Trim())}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", _key);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();

        // Immer als UTF-8 lesen (unabhängig vom Content-Type-Charset)
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var json = Encoding.UTF8.GetString(bytes);
        var antwort = JsonSerializer.Deserialize<WissenAntwort>(json, Opt);
        return antwort?.Treffer ?? new List<WissenTreffer>();
    }
}

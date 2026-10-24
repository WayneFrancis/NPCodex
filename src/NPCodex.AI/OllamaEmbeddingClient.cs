using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NPCodex.AI;

/// <summary>
/// Local Ollama text embeddings. Does not use the chat model; no cloud API
/// or third-party credentials are needed. Embedding model returns 768 floats.
/// </summary>
public sealed class OllamaEmbeddingClient : IDisposable
{
    public const string ModelName = "nomic-embed-text";
    public const int Dimensions = 768;

    private readonly HttpClient _client;

    public OllamaEmbeddingClient(string? baseUrl = null)
    {
        baseUrl ??= Environment.GetEnvironmentVariable("NPCODEX_OLLAMA_URL");
        baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://localhost:11434/"
            : baseUrl.TrimEnd('/') + "/";

        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    public Task<float[]> EmbedDocumentAsync(
        string content, CancellationToken cancellationToken = default) =>
        EmbedAsync("search_document: " + content, cancellationToken);

    public Task<float[]> EmbedQueryAsync(
        string question, CancellationToken cancellationToken = default) =>
        EmbedAsync("search_query: " + question, cancellationToken);

    private async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Text to embed cannot be blank.", nameof(input));

        // Do not silently truncate source memories or queries.
        using var response = await _client.PostAsJsonAsync(
            "api/embed", new { model = ModelName, input, truncate = false },
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "Ollama returned HTTP 404. Check Ollama at localhost:11434 and " +
                "install the model with 'ollama pull nomic-embed-text'.");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(
            cancellationToken: cancellationToken);
        var vector = payload?.Embeddings is { Length: 1 }
            ? payload.Embeddings[0]
            : null;

        if (vector is null || vector.Length != Dimensions)
            throw new InvalidOperationException(
                $"Expected one {Dimensions}-dimensional vector from Ollama; " +
                $"received {vector?.Length.ToString() ?? "none"} dimensions.");

        if (vector.Any(x => !float.IsFinite(x)) || vector.All(x => x == 0))
            throw new InvalidOperationException("Ollama returned an invalid embedding.");

        return vector;
    }

    public void Dispose() => _client.Dispose();

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("embeddings")]
        public float[][]? Embeddings { get; set; }
    }
}

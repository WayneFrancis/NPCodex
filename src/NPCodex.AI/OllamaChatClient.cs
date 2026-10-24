using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NPCodex.AI;

/// <summary>
/// Sends a single-turn, non-streaming chat request to a local Ollama model.
/// Does not save any dialogue or send data to cloud endpoints by default.
/// </summary>
public sealed class OllamaChatClient : IDisposable
{
    private readonly HttpClient _client;
    public string ModelName { get; }

    public OllamaChatClient(string? baseUrl = null, string? model = null)
    {
        baseUrl ??= Environment.GetEnvironmentVariable("NPCODEX_OLLAMA_URL");
        baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://localhost:11434/"
            : baseUrl.TrimEnd('/') + "/";

        ModelName = model ?? Environment.GetEnvironmentVariable("NPCODEX_CHAT_MODEL") ?? "qwen3:8b";
        if (string.IsNullOrWhiteSpace(ModelName))
            throw new ArgumentException("The chat model name must not be blank.", nameof(model));

        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public async Task<string> GenerateAsync(
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt) ||
            string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentException("System and user messages are required.");

        var payload = new
        {
            model = ModelName,
            stream = false,
            think = false, // Qwen3: return only its user-facing response.
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            options = new { temperature = 0.6, num_predict = 250 }
        };

        using var response = await _client.PostAsJsonAsync(
            "api/chat", payload, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"Ollama chat model '{ModelName}' could not be found. " +
                $"Check 'docker exec ollama ollama list' or pull the model.");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
            cancellationToken: cancellationToken);

        var text = result?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                $"Ollama model '{ModelName}' returned no dialogue content.");

        return text;
    }

    public void Dispose() => _client.Dispose();

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}

using System.ClientModel;
using System.Runtime.CompilerServices;
using OpenAI;
using OpenAI.Chat;

namespace InterviewAssistant.Services;

public enum LocalLlmProvider { Ollama, LmStudio }

public class LocalLLMService
{
    public static readonly Dictionary<LocalLlmProvider, Uri> DefaultEndpoints = new()
    {
        [LocalLlmProvider.Ollama]   = new Uri("http://localhost:11434/v1"),
        [LocalLlmProvider.LmStudio] = new Uri("http://localhost:1234/v1"),
    };

    public const string DefaultSystemPrompt =
        "Ты — эксперт-ментор на техническом собеседовании. " +
        "Отвечай кратко, структурировано, по существу. " +
        "Давай конкретные примеры кода или практики где уместно. " +
        "Отвечай на языке вопроса (русский или английский).";

    private readonly ChatClient _chatClient;
    private readonly string _systemPrompt;

    public LocalLLMService(LocalLlmProvider provider, string model,
                           string? customEndpoint = null, string? systemPrompt = null)
    {
        _systemPrompt = systemPrompt ?? DefaultSystemPrompt;

        var endpoint = !string.IsNullOrWhiteSpace(customEndpoint)
            ? new Uri(customEndpoint)
            : DefaultEndpoints[provider];

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        _chatClient = new OpenAIClient(new ApiKeyCredential("local"), options).GetChatClient(model);
    }

    public async IAsyncEnumerable<string> GetStreamingResponseAsync(
        string question,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ChatMessage[] messages =
        [
            new SystemChatMessage(_systemPrompt),
            new UserChatMessage(question)
        ];

        await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages, cancellationToken: ct))
            foreach (var part in update.ContentUpdate)
                if (!string.IsNullOrEmpty(part.Text))
                    yield return part.Text;
    }
}

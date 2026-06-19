using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Net.ServerSentEvents;
using System.Text.Json;

using var httpClient = new HttpClient()
{
    BaseAddress = new Uri("https://localhost:7017/")
};

var conversationId = Guid.NewGuid().ToString();

while (true)
{
    Console.Write("Question: ");
    var question = Console.ReadLine()!;

    using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat/streaming")
    {
        Content = JsonContent.Create(new
        {
            conversationId,
            message = question
        }, options: JsonSerializerOptions.Web)
    };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Text.EventStream));

    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    using var responseStream = await response.Content.ReadAsStreamAsync();

    var items = SseParser.Create(responseStream, ItemParser).EnumerateAsync();

    await foreach (var item in items)
    {
        if (item.EventType == "start")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;

            Console.WriteLine($"Conversation ID: {item.Data.ConversationId}");

            Console.WriteLine();
            Console.ResetColor();
        }

        if (item.EventType == "delta")
        {
            Console.Write(item.Data.Response);
        }
        else if (item.EventType == "metadata")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine($"Token count: {item.Data.TotalTokenCount}");

            Console.WriteLine();
            Console.ResetColor();
        }
    }
}

static ChatResponse ItemParser(string type, ReadOnlySpan<byte> data)
    => JsonSerializer.Deserialize<ChatResponse>(data, JsonSerializerOptions.Web)!;

public record class ChatResponse(string? ConversationId, string? Response, long? TotalTokenCount = null);

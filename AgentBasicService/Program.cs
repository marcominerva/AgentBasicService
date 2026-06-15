using System.ClientModel;
using System.Collections.Concurrent;
using System.Text.Json;
using AgentBasicService.Settings;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using TinyHelpers.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

var openAISettings = builder.Services.ConfigureAndGet<AzureOpenAISettings>(builder.Configuration, "AzureOpenAI")!;
builder.Services.AddChatClient(_ =>
{
    // Endpoint must end with /openai/v1 for Azure OpenAI
    var openAIClient = new OpenAIClient(new ApiKeyCredential(openAISettings.ApiKey), new() { Endpoint = new(openAISettings.Endpoint) });
    return openAIClient.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(openAISettings.Deployment);
});

builder.Services.AddAIAgent("Default", (services, key) =>
{
    var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
    var chatClient = services.GetRequiredService<IChatClient>();

    var chatHistoryProvider = new InMemoryChatHistoryProvider(new()
    {
        ChatReducer = new MessageCountingChatReducer(20), //new SummarizingChatReducer(chatClient, 1, 10)
        ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
        //ProvideOutputMessageFilter = messages =>
        //{
        //    // This method is called BEFORE actually sends the messages to the LLM, so we can filter out messages that we don't want the LLM to use.
        //    return messages.Where(m => m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider
        //        && m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory);
        //},
        StorageInputRequestMessageFilter = messages =>
        {
            // The messages list contains the request messages of the current turn, but it does not contain the response messages yet,
            // as we are still in the process of handling the request.
            // This method is called AFTER the response is received from the LLM, but before storing the response messages in the chat history,
            // so we can filter out request messages that we don't want to store.
            // For example, we can filter out messages from the AI Context Providers, as they can be re-generated if needed.
            // By default the chat history provider will store all messages, except for those that came from chat history in the first place.
            // We also want to maintain that exclusion here.
            return messages.Where(m => m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory
                && m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider);
        }
    });

    return chatClient.AsAIAgent(new()
    {
        Name = key,
        ChatOptions = new()
        {
            Instructions = "You are a helpful assistant that provides concise and accurate information."
        },
        AIContextProviders = [new RagProvider(httpContextAccessor)],
        ChatHistoryProvider = chatHistoryProvider
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
})
.WithSessionStore((services, key) =>
{
    var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
    var agentSessionStore = new CustomAgentSessionStore(httpContextAccessor);

    return agentSessionStore;
}, withIsolation: false);

builder.Services.AddAIAgent("Translator", (services, key) =>
{
    var chatClient = services.GetRequiredService<IChatClient>();

    var answerer = chatClient.AsAIAgent(name: "Answerer",
        instructions: "You are a helpful assistant.",
        loggerFactory: services.GetRequiredService<ILoggerFactory>(),
        services: services);

    var responseTranslator = chatClient.AsAIAgent(name: "ResponseTranslator",
        instructions: """
            You are a translator. You will receive a response that may be in any language.
            Your job is to translate it to English.
            If the text is already in English, return it as is.
            Return ONLY the translated text without any additional commentary.
            """,
        loggerFactory: services.GetRequiredService<ILoggerFactory>(),
        services: services);

    return AgentWorkflowBuilder.BuildSequential([answerer, responseTranslator]).AsAIAgent(name: key);
});

builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.MapOpenApi();
app.MapSwaggerUI(setupAction: options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", app.Environment.ApplicationName);
});

app.MapPost("/api/chat", async (ChatRequest request, [FromKeyedServices("Default")] AIAgent agent, [FromKeyedServices("Default")] AgentSessionStore store) =>
{
    var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
    var session = await store.GetSessionAsync(agent, conversationId);

    var response = await agent.RunAsync(request.Message, session);

    await store.SaveSessionAsync(agent, conversationId, session);

    return TypedResults.Ok(new ChatResponse(conversationId, response.Text));
});

app.MapPost("/api/translator", async (Translation request, [FromKeyedServices("Translator")] AIAgent agent) =>
{
    // For the sake of simplicity, we are not maintaining conversation threads in this endpoint.
    var response = await agent.RunAsync(request.Message);
    return TypedResults.Ok(new Translation(response.Messages.Last().Text));
});

app.Run();

public record class ChatRequest(string? ConversationId, string Message);

public record class ChatResponse(string ConversationId, string Response);

public record class Translation(string Message);

public sealed class CustomAgentSessionStore(IHttpContextAccessor httpContextAccessor) : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new();

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        JsonElement? sessionContent = sessions.TryGetValue(key, out var existingSession) ? existingSession : null;

        return sessionContent switch
        {
            null => await agent.CreateSessionAsync(cancellationToken),
            _ => await agent.DeserializeSessionAsync(sessionContent.Value, cancellationToken: cancellationToken),
        };
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        sessions[key] = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
    }

    private static string GetKey(string conversationId, string agentId)
        => $"{agentId}:{conversationId}";
}

public class RagProvider(IHttpContextAccessor httpContextAccessor) : MessageAIContextProvider
{
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Get relevant information from a knowledge base or other source. Here we hardcode it for simplicity.
        return ValueTask.FromResult<IEnumerable<ChatMessage>>(
            [new(ChatRole.User, "My name is Marco"), new(ChatRole.User, $"Today is {DateTime.Now}")]
        );
    }
}
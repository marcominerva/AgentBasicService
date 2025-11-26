using System.Collections.Concurrent;
using System.Text.Json;
using AgentBasicService.Settings;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using TinyHelpers.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

var openAiSettings = builder.Services.ConfigureAndGet<AzureOpenAISettings>(builder.Configuration, "AzureOpenAI")!;

builder.Services.AddChatClient(new AzureOpenAIClient(new(openAiSettings.Endpoint),
    new AzureKeyCredential(openAiSettings.ApiKey)).GetChatClient(openAiSettings.Deployment).AsIChatClient());

builder.Services.AddSingleton<CustomAgentThreadStore>();

builder.Services.AddAIAgent("Default", (services, key) =>
{
    var chatClient = services.GetRequiredService<IChatClient>();

    return chatClient.CreateAIAgent(new()
    {
        Name = key,
        Instructions = "You are a helpful assistant that provides concise and accurate information.",
        ChatMessageStoreFactory = context =>
        {
            //var reducer = new MessageCountingChatReducer(4);
            var reducer = new SummarizingChatReducer(chatClient, 1, 4);
            return new InMemoryChatMessageStore(reducer, context.SerializedState, context.JsonSerializerOptions,
                InMemoryChatMessageStore.ChatReducerTriggerEvent.AfterMessageAdded);
        }
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
})
.WithThreadStore((services, key) =>
{
    var agentThreadStore = services.GetRequiredService<CustomAgentThreadStore>();
    return agentThreadStore;
});

builder.Services.AddAIAgent("Translator", (services, key) =>
{
    var chatClient = services.GetRequiredService<IChatClient>();

    var answerer = chatClient.CreateAIAgent(name: "Answerer",
        instructions: "You are a helpful assistant.",
        loggerFactory: services.GetRequiredService<ILoggerFactory>(),
        services: services);

    var responseTranslator = chatClient.CreateAIAgent(name: "ResponseTranslator",
        instructions: """
            You are a translator. You will receive a response that may be in any language.
            Your job is to translate it to English.
            If the text is already in English, return it as is.
            Return ONLY the translated text without any additional commentary.
            """,
        loggerFactory: services.GetRequiredService<ILoggerFactory>(),
        services: services);

    return AgentWorkflowBuilder.BuildSequential([answerer, responseTranslator]).AsAgent(name: key);
});

builder.Services.AddSingleton(services => services.GetRequiredKeyedService<AIAgent>("Default"));
builder.Services.AddSingleton(services => services.GetRequiredKeyedService<AgentThreadStore>("Default"));

builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", app.Environment.ApplicationName);
});

app.MapPost("/api/chat", async (ChatRequest request, AIAgent agent, AgentThreadStore agentThreadStore) =>
{
    var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
    var thread = await agentThreadStore.GetThreadAsync(agent, conversationId);

    var response = await agent.RunAsync(request.Message, thread);

    await agentThreadStore.SaveThreadAsync(agent, conversationId, thread);

    // If you want to return structured output, uncomment the following code:
    // Also, you need to add the appropriate Description attributes to the ChatResponse record.
    //var jsonSerializerOptions = new JsonSerializerOptions
    //{
    //    PropertyNameCaseInsensitive = true,
    //    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    //    Converters = { new JsonStringEnumConverter() }
    //};

    //var structuredOutput = await agent.RunAsync(request.Message, options: new ChatClientAgentRunOptions
    //{
    //    ChatOptions = new()
    //    {
    //        ResponseFormat = ChatResponseFormat.ForJsonSchema<ChatResponse>(jsonSerializerOptions)
    //    }
    //});

    //var result = structuredOutput.Deserialize<ChatResponse>(jsonSerializerOptions);

    return TypedResults.Ok(new ChatResponse(conversationId, response.Text));
});

app.MapPost("/api/translator", async (ChatRequest request, [FromKeyedServices("Translator")] AIAgent agent) =>
{
    // For the sake of simplicity, we are not maintaining conversation threads in this endpoint.
    var response = await agent.RunAsync(request.Message);
    return TypedResults.Ok(new ChatResponse(string.Empty, response.Messages.LastOrDefault()?.Text!));
});

app.Run();

public record class ChatRequest(string? ConversationId, string Message);

public record class ChatResponse(string ConversationId, string Response);

public sealed class CustomAgentThreadStore(IHttpContextAccessor httpContextAccessor) : AgentThreadStore
{
    private readonly ConcurrentDictionary<string, JsonElement> threads = new();

    /// <inheritdoc/>
    public override ValueTask SaveThreadAsync(AIAgent agent, string conversationId, AgentThread thread, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        threads[key] = thread.Serialize();
        return default;
    }

    /// <inheritdoc/>
    public override ValueTask<AgentThread> GetThreadAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        JsonElement? threadContent = threads.TryGetValue(key, out var existingThread) ? existingThread : null;

        return threadContent switch
        {
            null => new ValueTask<AgentThread>(agent.GetNewThread()),
            _ => new ValueTask<AgentThread>(agent.DeserializeThread(threadContent.Value)),
        };
    }

    private static string GetKey(string conversationId, string agentId)
        => $"{agentId}:{conversationId}";
}
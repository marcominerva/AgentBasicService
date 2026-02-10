using System.ClientModel;
using System.Collections.Concurrent;
using System.Text.Json;
using AgentBasicService.Settings;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using TinyHelpers.AspNetCore.Extensions;
using static Microsoft.Agents.AI.InMemoryChatHistoryProvider;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

var openAISettings = builder.Services.ConfigureAndGet<AzureOpenAISettings>(builder.Configuration, "AzureOpenAI")!;
builder.Services.AddChatClient(_ =>
{
    // Endpoint must end with /openai/v1 for Azure OpenAI
    var openAIClient = new OpenAIClient(new ApiKeyCredential(openAISettings.ApiKey), new() { Endpoint = new(openAISettings.Endpoint) });
    return openAIClient.GetChatClient(openAISettings.Deployment).AsIChatClient();
});

builder.Services.AddAIAgent("Default", (services, key) =>
{
    var chatClient = services.GetRequiredService<IChatClient>();

    return chatClient.AsAIAgent(new()
    {
        Name = key,
        ChatOptions = new()
        {
            Instructions = "You are a helpful assistant that provides concise and accurate information."
        },
        AIContextProviderFactory = (context, cancellationToken) => ValueTask.FromResult<AIContextProvider>(new RagProvider()),
        ChatHistoryProviderFactory = (context, cancellationToken) =>
        {
            //var reducer = new MessageCountingChatReducer(4);
            var reducer = new SummarizingChatReducer(chatClient, 1, 4);
            var store = new InMemoryChatHistoryProvider(reducer, context.SerializedState, context.JsonSerializerOptions, InMemoryChatHistoryProvider.ChatReducerTriggerEvent.AfterMessageAdded)
                //.WithAIContextProviderMessageRemoval()
                ;

            return ValueTask.FromResult<ChatHistoryProvider>(store);
        }
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
})
.WithSessionStore((services, key) =>
{
    var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
    var agentSessionStore = new CustomAgentSessionStore(httpContextAccessor);

    return agentSessionStore;
});

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

    return AgentWorkflowBuilder.BuildSequential([answerer, responseTranslator]).AsAgent(name: key);
});

builder.Services.AddSingleton(services => services.GetRequiredKeyedService<AIAgent>("Default"));
builder.Services.AddSingleton(services => services.GetRequiredKeyedService<AgentSessionStore>("Default"));

builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", app.Environment.ApplicationName);
});

app.MapPost("/api/chat", async (ChatRequest request, AIAgent agent, AgentSessionStore store) =>
{
    var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
    var session = await store.GetSessionAsync(agent, conversationId);

    var response = await agent.RunAsync(request.Message, session);

    await store.SaveSessionAsync(agent, conversationId, thread);

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

public sealed class CustomAgentSessionStore(IHttpContextAccessor httpContextAccessor) : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new();

    public override ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        sessions[key] = session.Serialize();
        return default;
    }

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        JsonElement? threadContent = sessions.TryGetValue(key, out var existingThread) ? existingThread : null;

        return threadContent switch
        {
            null => await agent.CreateSessionAsync(cancellationToken),
            _ => await agent.DeserializeSessionAsync(threadContent.Value),
        };
    }

    private static string GetKey(string conversationId, string agentId)
        => $"{agentId}:{conversationId}";
}

public class RagProvider : AIContextProvider
{
    public override ValueTask<AIContext> InvokingAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Get relevant information from a knowledge base or other source. Here we hardcode it for simplicity.

        //var input = new ChatMessage(ChatRole.User, $"""
        //    Conosci solo queste informazioni:
        //    ---
        //    Il centro storico di Taggia è situato nell'immediato entroterra della valle Argentina, mentre l'abitato di Arma è una località balneare. Tra i due centri vi è la zona denominata Levà (il toponimo deriva dalla denominazione romana per indicare un'area rialzata).
        //    Il territorio comunale è tuttavia molto esteso, perché coincide con la bassa valle del torrente Argentina, dalla confluenza del torrente Oxentina, presso la località San Giorgio, fino al mare. Si tratta di un ampio settore di entroterra caratterizzato da estese colture - soprattutto oliveti - nella fascia collinare e da estesi boschi nella sua porzione montana, che raggiunge il monte Faudo, massima elevazione del comune con i suoi 1149 metri.
        //    Altre vette del territorio il monte Follia (1031 m), il monte Neveia (835 m), il monte Santa Maria (462 m), il monte Giamanassa (405 m).
        //    """);

        //return ValueTask.FromResult(new AIContext
        //{
        //    Messages = [input]
        //});

        return ValueTask.FromResult(new AIContext
        {
            Messages = [new(ChatRole.User, "My name is Marco"), new(ChatRole.User, $"Today is {DateTime.Now}")]
        });
    }
}
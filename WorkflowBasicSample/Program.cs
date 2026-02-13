using System.ClientModel;
using System.Globalization;
using System.Text;
using Azure.AI.OpenAI;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lingua;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using WorkflowBasicSample;
using static SummarizeExecutor;
using static TranslationExecutor;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(new AzureOpenAIClient(new(Constants.TranscribeEndpoint), new ApiKeyCredential(Constants.ApiKey)).GetAudioClient(Constants.TranscribeDeploymentName));
builder.Services.AddChatClient(new OpenAIClient(new ApiKeyCredential(Constants.ApiKey), new() { Endpoint = new Uri(Constants.ChatEndpoint) })
            .GetChatClient(Constants.ChatDeploymentName).AsIChatClient());

builder.Services.AddAIAgent("TranslatorAgent", (services, key) =>
{
    var chatClient = services.GetRequiredService<IChatClient>();

    var agent = chatClient.AsAIAgent(new()
    {
        Name = key,
        ChatOptions = new()
        {
            Instructions = "You are an Assistant that translates text in Italian. The user will provide the text. You should only respond with the translated text without any additional information or formatting.",
        }
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);

    return agent;
});

builder.Services.AddAIAgent("SummarizationAgent", (services, key) =>
{
    var chatClient = services.GetRequiredService<IChatClient>();

    var agent = chatClient.AsAIAgent(new()
    {
        Name = key,
        ChatOptions = new()
        {
            Instructions = """
                You are an assistant that reviews and refines audio transcriptions, producing a polished document with clear and fluent text.
                Correct any speech errors or inaccuracies, and ensure the content is cohesive. Be sure to keep all the information and the context.
                When you create the document, remove any redundant phrases or filler words commonly found in spoken language. Pay attention to ensuring proper grammar, punctuation, and sentence structure.
                If any speaker uses first-person language (e.g., ‘I’, ‘we’), rewrite the content so that all statements are expressed in the third person in the document. Maintain consistency in style and clarity throughout the text.
                Do not reference that the text originated from a transcription or mention any processing steps—simply present the finalized document.
                """,
            Temperature = 0
        }
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);

    return agent;
});

builder.AddWorkflow("TranscriptionManager", (services, key) =>
{
    var audioClient = services.GetRequiredService<AudioClient>();
    var translatorAgent = services.GetRequiredKeyedService<AIAgent>("TranslatorAgent");
    var summarizationAgent = services.GetRequiredKeyedService<AIAgent>("SummarizationAgent");

    var transcribeExecutor = new TranscribeExecutor(audioClient, services.GetRequiredService<ILogger<TranscribeExecutor>>());
    var translatorExecutor = new TranslationExecutor(translatorAgent, services.GetRequiredService<ILogger<TranslationExecutor>>());
    var summarizeExecutor = new SummarizeExecutor(summarizationAgent, services.GetRequiredService<ILogger<SummarizeExecutor>>());
    var createDocumentExecutor = new CreateDocumentExecutor(services.GetRequiredService<ILogger<CreateDocumentExecutor>>());

    var workflow = new WorkflowBuilder(transcribeExecutor).WithName(key)
        // If the identified language is not Italian, go through the translator
        .AddEdge<TranscriptionResult>(transcribeExecutor, translatorExecutor, result => result!.Language != "it")
        // If the identified language is Italian, go directly to summarization
        .AddEdge<TranscriptionResult>(transcribeExecutor, summarizeExecutor, result => result!.Language == "it")
        // The translator always goes to summarization
        .AddEdge(translatorExecutor, summarizeExecutor)
        .WithOutputFrom(summarizeExecutor)
        // The summary goes to DOCX creation
        .AddEdge(summarizeExecutor, createDocumentExecutor)
        .WithOutputFrom(createDocumentExecutor)
        .Build();

    return workflow;
});

var app = builder.Build();

var workflow = app.Services.GetRequiredKeyedService<Workflow>("TranscriptionManager");
var logger = app.Services.GetRequiredService<ILogger<Program>>();

try
{
    using var input = File.OpenRead(@"D:\Audio.mp3");
    await using var run = await InProcessExecution.StreamAsync(workflow, input);

    await foreach (var @event in run.WatchStreamAsync())
    {
        logger.LogDebug("Event received: {Event}", @event.GetType().Name);

        if (@event is ExecutorCompletedEvent executorComplete && executorComplete.Data is not null && executorComplete.Data is not Stream) // Data can be null for executors with no output.
        {
            // An executor has completed its task.
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{Environment.NewLine}{executorComplete.ExecutorId}{Environment.NewLine}{executorComplete.Data}{Environment.NewLine}");
        }
        else if (@event is WorkflowOutputEvent workflowOutputEvent)
        {
            // This is the final output of the workflow.
            Console.ForegroundColor = ConsoleColor.Green;

            if (workflowOutputEvent.Data is AgentResponseUpdate response)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(response);
            }
            else if (workflowOutputEvent.Data is Stream docxStream)
            {
                var outputPath = Path.Combine(Path.GetTempPath(), $"Summary_{DateTime.Now:yyyyMMdd_HHmmss}.docx");
                await using var fileStream = File.Create(outputPath);
                await docxStream.CopyToAsync(fileStream);

                Console.WriteLine($"{Environment.NewLine}{Environment.NewLine}Summary saved to: {outputPath}");
            }

            Console.ResetColor();
        }
        else if (@event is WorkflowErrorEvent workflowErrorEvent)
        {
            logger.LogError("Workflow error: {Error}", workflowErrorEvent.Data);
        }
    }
}
catch (Exception ex)
{
    Console.ResetColor();
    logger.LogError(ex, "An error occurred during workflow execution.");
}

public class TranscribeExecutor(AudioClient audioClient, ILogger<TranscribeExecutor> logger) : Executor<FileStream, TranscriptionResult>(nameof(TranscribeExecutor))
{
    public override async ValueTask<TranscriptionResult> HandleAsync(FileStream message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting transcription...");

        if (message.CanSeek)
        { 
            message.Position = 0;
        }

        var transcription = await audioClient.TranscribeAudioAsync(message, "Input.mp3", cancellationToken: cancellationToken);
        var content = transcription.Value.Text;

        var detector = LanguageDetectorBuilder.FromLanguages(Language.English, Language.Italian).Build();
        var detectedLanguage = detector.DetectLanguageOf(content) switch
        {
            Language.Italian => "it",
            _ => "en"
        };

        logger.LogInformation("Transcription completed. Detected language: {Language}", detectedLanguage);

        return new(content, detectedLanguage);
    }
}

public class TranslationExecutor(AIAgent agent, ILogger<TranslationExecutor> logger) : Executor<TranscriptionResult, TranscriptionResult>(nameof(TranslationExecutor))
{
    public override async ValueTask<TranscriptionResult> HandleAsync(TranscriptionResult message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Translating text from {Language} to Italian...", new CultureInfo(message.Language).EnglishName);

        var response = await agent.RunAsync(message.Text, cancellationToken: cancellationToken);
        logger.LogInformation("Translation completed.");

        return new(response.Text, "it");
    }
}

class SummarizeExecutor(AIAgent agent, ILogger<SummarizeExecutor> logger) : Executor<TranscriptionResult>(nameof(TranscriptionResult))
{
    public override async ValueTask HandleAsync(TranscriptionResult message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting summarization...");

        var response = agent.RunStreamingAsync(message.Text, cancellationToken: cancellationToken);

        var content = new StringBuilder();
        await foreach (var update in response)
        {
            content.Append(update.Text);
            await context.YieldOutputAsync(update, cancellationToken);
        }

        await context.SendMessageAsync(content.ToString(), cancellationToken);
    }
}

public class CreateDocumentExecutor(ILogger<CreateDocumentExecutor> logger) : Executor<string, Stream>(nameof(CreateDocumentExecutor))
{
    public override ValueTask<Stream> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating document from summarized text...");

        var stream = new MemoryStream();

        using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        var paragraph = body.AppendChild(new Paragraph());
        var run = paragraph.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Run());
        run.AppendChild(new Text(message));

        mainPart.Document.Save();
        document.Dispose();
        stream.Position = 0;

        logger.LogInformation("Document creation completed.");

        return ValueTask.FromResult<Stream>(stream);
    }
}

public record class TranscriptionResult(string Text, string Language);

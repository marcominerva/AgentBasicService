using System.Text;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddWorkflow("SampleWorkflow", (services, key) =>
{
    var uppercaseExecutor = new UppercaseExecutor();
    var caesarCipherExecutor = new CaesarCipherExecutor();
    var removeSpaceExecutor = new RemoveSpaceExecutor();
    var reverseTextExecutor = new ReverseTextExecutor();

    var workflow = new WorkflowBuilder(uppercaseExecutor).WithName(key)
        .AddEdge(uppercaseExecutor, caesarCipherExecutor)
        .AddEdge<string>(caesarCipherExecutor, reverseTextExecutor, result => !result!.Contains(' '))
        .AddEdge<string>(caesarCipherExecutor, removeSpaceExecutor, result => result!.Contains(' '))
        .AddEdge(removeSpaceExecutor, reverseTextExecutor)
        .WithOutputFrom(reverseTextExecutor)
        .Build();

    return workflow;
});

var app = builder.Build();

var workflow = app.Services.GetRequiredKeyedService<Workflow>("SampleWorkflow");

while (true)
{
    Console.Write("Enter text to process: ");
    var input = Console.ReadLine()!;

    await using var run = await InProcessExecution.StreamAsync(workflow, input);

    await foreach (var @event in run.WatchStreamAsync())
    {
        Console.WriteLine($"Event: {@event.GetType().Name}");

        if (@event is ExecutorCompletedEvent executorCompletedEvent)
        {
            Console.WriteLine($"Executor '{executorCompletedEvent.ExecutorId}' completed with result: {executorCompletedEvent.Data}");
        }
        else if (@event is WorkflowOutputEvent workflowOutputEvent)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Final Result: {workflowOutputEvent.Data}");
            Console.ResetColor();
        }
    }

    Console.WriteLine();
}

public class UppercaseExecutor() : Executor<string, string>(nameof(UppercaseExecutor))
{
    public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = message.ToUpperInvariant();
        return ValueTask.FromResult(result);
    }
}

public class CaesarCipherExecutor() : Executor<string, string>(nameof(CaesarCipherExecutor))
{
    // Alfabeto esteso con lettere accentate italiane
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZÀÈÉÌÒÙ0123456789";

    public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = new StringBuilder();

        foreach (var c in message)
        {
            var index = Alphabet.IndexOf(c);
            if (index != -1)
            {
                var newIndex = (index + 3) % Alphabet.Length;
                result.Append(Alphabet[newIndex]);
            }
            else
            {
                // Keep characters not in the alphabet unchanged, like spaces and punctuation.
                result.Append(c);
            }
        }

        return ValueTask.FromResult(result.ToString());
    }
}

public class ReverseTextExecutor() : Executor<string,string>(nameof(ReverseTextExecutor))
{
    public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = string.Concat(message.Reverse());
        return ValueTask.FromResult(result);
    }
}

public class RemoveSpaceExecutor() : Executor<string, string>(nameof(RemoveSpaceExecutor))
{
    private static readonly char[] replacementChars = ['o', '¤', '¥', '¦', '§', '¨', 'µ', '¶', 'ƒ', '—', '•', '…', '‰'];

    public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = string.Concat(message.Select(c => c == ' ' ? replacementChars[Random.Shared.Next(replacementChars.Length)] : c));
        return ValueTask.FromResult(result);
    }
}
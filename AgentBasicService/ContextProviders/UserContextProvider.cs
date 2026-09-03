using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public class UserContextProvider(IHttpContextAccessor httpContextAccessor) : MessageAIContextProvider
{
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Get relevant information from a knowledge base or other source. Here we hardcode it for simplicity.
        return ValueTask.FromResult<IEnumerable<ChatMessage>>(
            [new(ChatRole.User, "My name is Marco"), new(ChatRole.User, $"Today is {DateTime.Now}")]
        );
    }
}
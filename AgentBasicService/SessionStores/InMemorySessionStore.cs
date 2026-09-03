using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

public sealed class InMemorySessionStore(IHttpContextAccessor httpContextAccessor) : AgentSessionStore
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

    public override ValueTask DeleteSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        sessions.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    private static string GetKey(string conversationId, string agentId)
        => $"{agentId}:{conversationId}";
}

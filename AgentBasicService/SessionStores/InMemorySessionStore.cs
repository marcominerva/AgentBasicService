using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentBasicService.SessionStores;

public sealed class InMemorySessionStore(IHttpContextAccessor httpContextAccessor) : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new();

    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var conversationId = GetKey(agent, key);
        var sessionContent = sessions.TryGetValue(conversationId, out var session)
            ? await agent.DeserializeSessionAsync(session, cancellationToken: cancellationToken) : null;

        return sessionContent;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        var conversationId = GetKey(agent, key);
        sessions[conversationId] = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
    }

    private static string GetKey(AIAgent agent, AgentSessionStoreKey key)
    {
        if (key.Partitions?.TryGetValue("isolation", out var isolationKey) == true)
        {
            return $"{agent.Id}:{isolationKey}:{key.SessionId}";
        }

        return $"{agent.Id}:{key.SessionId}";
    }
}

using System.Text.Json;
using OpenMetaverse;

namespace TasiaAddons.InternalScriptRpc;

/// <summary>Explicit typed destination-operation registry exposed to trusted region addons.</summary>
public interface IInternalScriptRpcRegistry
{
    void RegisterOperation<TRequest, TResponse>(string name, Func<UUID, TRequest, TResponse> handler,
        TimeSpan? executionTimeout = null);
}

/// <summary>Trusted in-process API for server modules that must use the same authenticated RPC route as scripts.</summary>
public interface IInternalScriptRpcClient
{
    string InvokeForOwner(UUID owner, UUID targetRegion, string operation, string payloadJson);
}

/// <summary>Wire payload for the built-in SmartNPC transfer operation.</summary>
public sealed class NpcTransferRpcRequest
{
    public string PackedNpcJson { get; set; } = string.Empty;
}

/// <summary>Wire response for the built-in SmartNPC transfer operation.</summary>
public sealed class NpcTransferRpcResponse
{
    public bool Success { get; set; }
    public string Reason { get; set; } = string.Empty;
}

using System.Text.Json;

namespace MSPChallenge_Client_Browser.Models;

/// <summary>
/// A single message received over the game WebSocket.
/// The <see cref="Payload"/> element is a self-contained clone that survives
/// beyond the lifetime of the original parse document.
/// </summary>
public sealed record WsMessage(
    string      HeaderName,
    JsonElement Payload,
    string      RawJson,
    DateTime    ReceivedAt);

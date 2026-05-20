using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Manages the persistent WebSocket connection to the MSP Challenge game server.
/// <para>
/// Usage:
/// 1. Call <see cref="ConnectAndSubscribeAsync"/> once after all layers are loaded.
/// 2. Subscribe to <see cref="MessageReceived"/> to react to incoming messages.
///    The event fires on a background thread — use InvokeAsync when updating Blazor state.
/// 3. Stored messages are available via <see cref="GameLatestMessages"/>,
///    <see cref="ExecuteBatchMessages"/>, and <see cref="ImmersiveSessionsMessages"/>.
/// </para>
/// </summary>
public sealed class GameWebSocketService : IAsyncDisposable
{
    // ── Stored messages by type ────────────────────────────────────────────────
    private readonly List<WsMessage> _gameLatest        = new();
    private readonly List<WsMessage> _executeBatch      = new();
    private readonly List<WsMessage> _immersiveSessions = new();

    public IReadOnlyList<WsMessage> GameLatestMessages        => _gameLatest;
    public IReadOnlyList<WsMessage> ExecuteBatchMessages      => _executeBatch;
    public IReadOnlyList<WsMessage> ImmersiveSessionsMessages => _immersiveSessions;

    // ── Connection state ───────────────────────────────────────────────────────
    private ClientWebSocket?            _ws;
    private CancellationTokenSource     _cts = new();

    public WebSocketState State => _ws?.State ?? WebSocketState.None;

    // ── Events ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Raised on a background thread each time a recognised message is stored.
    /// Callers must marshal back to the Blazor UI thread (e.g. InvokeAsync).
    /// </summary>
    public event Action<WsMessage>? MessageReceived;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the WebSocket with the required authentication headers, sends the
    /// subscription message, and begins the background receive loop.
    /// </summary>
    public async Task ConnectAndSubscribeAsync(
        string wsAddress,
        string accessToken,
        int    gameSessionId,
        int    teamId,
        int    userId)
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization",   $"Bearer {accessToken}");
        _ws.Options.SetRequestHeader("Game-Session-Id", gameSessionId.ToString());
        _ws.Options.SetRequestHeader("GameSessionId",   gameSessionId.ToString());

        await _ws.ConnectAsync(new Uri(wsAddress), _cts.Token);

        // Initiate server-side subscription
        var sub = JsonSerializer.Serialize(new
        {
            team_id          = teamId,
            user             = userId,
            last_update_time = 0.0
        });
        await _ws.SendAsync(
            Encoding.UTF8.GetBytes(sub),
            WebSocketMessageType.Text,
            endOfMessage: true,
            _cts.Token);

        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    // ── Background receive loop ────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_ws is null) return;

        var buffer = new byte[64 * 1024];

        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Server closed connection",
                            CancellationToken.None);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                StoreMessage(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
        catch { /* connection lost — state is now Closed/Aborted */ }
    }

    private void StoreMessage(string raw)
    {
        try
        {
            using var doc  = JsonDocument.Parse(raw);
            var root       = doc.RootElement;

            // The protocol uses "header_name" as the type discriminator.
            // Fall back to "type" and "headerName" for future-proofing.
            var headerName = TryGetStringProp(root, "header_type")
                          ?? TryGetStringProp(root, "header_name")
                          ?? TryGetStringProp(root, "type")
                          ?? string.Empty;

            var payload = root.TryGetProperty("payload", out var p)
                ? p.Clone()
                : root.Clone();

            var msg = new WsMessage(headerName, payload, raw, DateTime.UtcNow);

            switch (headerName)
            {
                case "Game/Latest":
                    lock (_gameLatest)        _gameLatest.Add(msg);
                    break;

                case "Batch/ExecuteBatch":
                    lock (_executeBatch)      _executeBatch.Add(msg);
                    break;

                case "ImmersiveSessions/Update":
                    lock (_immersiveSessions) _immersiveSessions.Add(msg);
                    break;

                default:
                    // Unknown type — not stored; add a catch-all list here when needed.
                    return;
            }

            MessageReceived?.Invoke(msg);
        }
        catch { /* malformed message — skip */ }
    }

    private static string? TryGetStringProp(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString()
           : null;

    // ── Disconnect (navigate-away / reset) ────────────────────────────────────

    /// <summary>
    /// Closes the WebSocket and resets internal state so the service can be
    /// reused for a fresh <see cref="ConnectAndSubscribeAsync"/> call.
    /// </summary>
    public async Task StopAsync()
    {
        await _cts.CancelAsync();

        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Navigating home",
                        CancellationToken.None);
            }
            catch { }

            _ws.Dispose();
            _ws = null;
        }

        _cts.Dispose();
        _cts = new CancellationTokenSource();

        _gameLatest.Clear();
        _executeBatch.Clear();
        _immersiveSessions.Clear();
    }

    // ── Disposal ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Dispose",
                        CancellationToken.None);
            }
            catch { }

            _ws.Dispose();
        }

        _cts.Dispose();
    }
}

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DigiChat.Domain;
using DigiChat.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigiChat.Infrastructure.Twitch;

/// <summary>
/// Maintains the EventSub WebSocket (wss://eventsub.wss.twitch.tv/ws), the
/// channel.chat.message subscription, keepalive monitoring, and the documented
/// reconnect handover. Messages missed while disconnected are gone (Twitch
/// does not replay over WebSocket); a chatter's next message simply admits
/// them, which the spec accepts (§16).
/// </summary>
public class TwitchEventSubService(
    TwitchAuthService auth,
    TwitchStatus status,
    AdmissionService admission,
    IHttpClientFactory httpFactory,
    IOptions<TwitchOptions> options,
    ILogger<TwitchEventSubService> logger) : BackgroundService
{
    private const string WsUrl = "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30";
    private const string HelixSubscriptions = "https://api.twitch.tv/helix/eventsub/subscriptions";
    private const string BacklogFullStatus = "Connected — chat backlog full (see log)";
    private static readonly TimeSpan[] AdmissionRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];
    private readonly Channel<ChatMessageEvent> _admissionQueue = Channel.CreateBounded<ChatMessageEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    /// <summary>
    /// Chatter IDs with a message already waiting in <see cref="_admissionQueue"/>.
    /// Admission is per viewer per session, so a second queued message from the
    /// same chatter can only ever return AlreadyParticipant — but it still
    /// consumes one of 256 slots. On a busy stream the regulars would otherwise
    /// crowd out a first-time viewer, and the symptom is the worst kind:
    /// someone chats and never gets a Digimon.
    /// </summary>
    private readonly HashSet<string> _pendingChatterIds = [];
    private readonly object _pendingSync = new();
    private volatile string _connectedStatus = "Connected";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (options.Value.MockMode)
        {
            status.Status = "Mock mode (no Twitch connection)";
            logger.LogInformation("Twitch integration disabled: mock mode");
            return;
        }

        var admissionWorker = ProcessAdmissionsAsync(ct);
        var backoff = new ReconnectBackoffState();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunConnectionAsync(backoff, ct);
                    if (ct.IsCancellationRequested) break;
                    logger.LogWarning("EventSub connection closed; retrying in {Backoff}", backoff.CurrentDelay);
                    status.Status = "Disconnected — retrying";
                    await Task.Delay(backoff.CurrentDelay, ct);
                    backoff.AdvanceAfterFailure();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "EventSub connection failed; retrying in {Backoff}", backoff.CurrentDelay);
                    status.Status = $"Disconnected — retrying ({ex.GetType().Name})";
                    await Task.Delay(backoff.CurrentDelay, ct);
                    backoff.AdvanceAfterFailure();
                }
            }
        }
        finally
        {
            _admissionQueue.Writer.TryComplete();
            try { await admissionWorker; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            status.Status = "Stopped";
        }
    }

    private async Task RunConnectionAsync(ReconnectBackoffState backoff, CancellationToken ct)
    {
        status.Status = "Authenticating…";
        var token = await auth.GetValidTokenAsync(ct);
        logger.LogInformation("Authenticated to Twitch as {Login} ({UserId})", token.Login, token.UserId);

        status.Status = "Connecting to EventSub…";
        var ws = new ClientWebSocket();

        var keepaliveTimeout = TimeSpan.FromSeconds(45); // updated from the welcome message
        var subscribed = false;

        try
        {
            await ConnectWithTimeoutAsync(ws, new Uri(WsUrl), ct);
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var json = await ReceiveMessageAsync(ws, keepaliveTimeout, ct);
                if (json is null)
                {
                    logger.LogWarning("EventSub keepalive timeout — assuming connection lost");
                    status.Status = "Keepalive timeout — reconnecting";
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                var metadata = doc.RootElement.GetProperty("metadata");
                var messageType = metadata.GetProperty("message_type").GetString();

                switch (messageType)
                {
                    case "session_welcome":
                    {
                        var session = doc.RootElement.GetProperty("payload").GetProperty("session");
                        var sessionId = session.GetProperty("id").GetString()!;
                        if (session.TryGetProperty("keepalive_timeout_seconds", out var ka) &&
                            ka.ValueKind == JsonValueKind.Number)
                            keepaliveTimeout = TimeSpan.FromSeconds(ka.GetInt32() + 15);

                        if (!subscribed)
                        {
                            await CreateSubscriptionAsync(token, sessionId, ct);
                            subscribed = true;
                        }
                        // A validated welcome and successful subscription are the
                        // point at which this connection is actually established.
                        // If the socket then faults, retry from the initial delay.
                        backoff.MarkSessionEstablished();
                        _connectedStatus = $"Connected (listening to #{token.Login})";
                        status.Status = _connectedStatus;
                        logger.LogInformation("EventSub session {SessionId} established", sessionId);
                        break;
                    }
                    case "session_keepalive":
                        break; // receipt itself resets the watchdog

                    case "session_reconnect":
                    {
                        // Documented handover: connect to the provided URL, wait for
                        // its welcome, then drop the old socket. Subscriptions carry over.
                        var url = doc.RootElement.GetProperty("payload").GetProperty("session")
                            .GetProperty("reconnect_url").GetString()!;
                        logger.LogInformation("EventSub requested reconnect; performing handover");
                        status.Status = "Reconnecting (server-requested)…";
                        var newWs = new ClientWebSocket();
                        string? welcome;
                        try
                        {
                            await ConnectWithTimeoutAsync(newWs, new Uri(url), ct);
                            welcome = await ReceiveMessageAsync(newWs, TimeSpan.FromSeconds(30), ct);
                        }
                        catch
                        {
                            newWs.Dispose();
                            throw;
                        }
                        // Twitch's handover protocol says the replacement sends
                        // session_welcome first. Take the old socket down only
                        // once that is confirmed — abandoning a working
                        // connection for an unverified one loses live chat.
                        if (welcome is null || !IsSessionWelcome(welcome))
                        {
                            newWs.Dispose();
                            logger.LogWarning(
                                "Reconnect handover did not begin with session_welcome; keeping the current "
                                + "connection and starting a fresh one");
                            return;
                        }
                        var old = ws;
                        ws = newWs;
                        try { old.Abort(); old.Dispose(); } catch { /* best effort */ }
                        _connectedStatus = $"Connected (listening to #{token.Login})";
                        status.Status = _connectedStatus;
                        break;
                    }
                    case "notification":
                        try
                        {
                            QueueNotification(metadata, doc.RootElement.GetProperty("payload"), token.UserId);
                        }
                        catch (Exception ex)
                        {
                            // One malformed/application message must not tear down
                            // transport and lose unrelated chat during reconnect.
                            logger.LogError(ex, "Could not parse an EventSub notification; connection remains open");
                        }
                        break;

                    case "revocation":
                        logger.LogWarning("EventSub subscription revoked: {Json}", json);
                        status.Status = "Subscription revoked — reconnecting";
                        return;

                    default:
                        logger.LogDebug("Unhandled EventSub message type {Type}", messageType);
                        break;
                }
            }
        }
        finally
        {
            try { ws.Dispose(); } catch { /* best effort */ }
        }
    }

    private void QueueNotification(
        JsonElement metadata, JsonElement payload, string broadcasterUserId)
    {
        var subscriptionType = metadata.GetProperty("subscription_type").GetString();
        if (subscriptionType != "channel.chat.message") return;

        // EventSub delivery ID — the official redelivery-dedup key (spec §15).
        var eventSubMessageId = metadata.GetProperty("message_id").GetString()!;
        var ev = payload.GetProperty("event");

        var sourceBroadcaster = ev.TryGetProperty("source_broadcaster_user_id", out var sb) &&
                                sb.ValueKind == JsonValueKind.String
            ? sb.GetString()
            : null;
        var fromOtherChannel = sourceBroadcaster is not null && sourceBroadcaster != broadcasterUserId;

        var msg = new ChatMessageEvent(
            MessageId: eventSubMessageId,
            TwitchUserId: ev.GetProperty("chatter_user_id").GetString()!,
            Login: ev.GetProperty("chatter_user_login").GetString() ?? "",
            DisplayName: ev.GetProperty("chatter_user_name").GetString() ?? "",
            IsFromOtherChannel: fromOtherChannel);

        lock (_pendingSync)
        {
            // Already queued for this chatter: nothing new to decide.
            if (!_pendingChatterIds.Add(msg.TwitchUserId)) return;
        }

        if (!_admissionQueue.Writer.TryWrite(msg))
        {
            lock (_pendingSync) _pendingChatterIds.Remove(msg.TwitchUserId);
            logger.LogError(
                "Admission queue is full; dropping chat event {MessageId} from {Login}",
                msg.MessageId, msg.Login);
            status.Status = BacklogFullStatus;
        }
    }

    private async Task ProcessAdmissionsAsync(CancellationToken ct)
    {
        await foreach (var msg in _admissionQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                var result = await TryHandleAdmissionWithRetryAsync(
                    token => admission.HandleAsync(msg, token), msg, logger, ct);
                if (result is not null)
                    logger.LogDebug("Chat message from {Login}: {Outcome}", msg.Login, result.Outcome);

                // Overflow is an operator warning, not a permanent connection
                // state. Clear it once the bounded channel has capacity again,
                // without overwriting a newer disconnect/revocation status.
                //
                // This lives INSIDE the try on purpose. The status setter raises
                // StatusChanged synchronously and the logger can fault, and an
                // escape from here kills the consumer for the rest of the
                // process: pending chatter IDs are never released, the queue
                // fills, and the next session_welcome resets the panel to
                // "Connected" while no admission is happening at all.
                if (status.Status == BacklogFullStatus && _admissionQueue.Reader.Count < 256)
                {
                    logger.LogInformation("Admission queue recovered below its capacity");
                    status.Status = _connectedStatus;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Admission retry worker failed unexpectedly for EventSub message {MessageId}; transport remains connected",
                    msg.MessageId);
            }
            finally
            {
                // Released only after the attempt finishes, so a chatter's next
                // message can retry if this one failed.
                lock (_pendingSync) _pendingChatterIds.Remove(msg.TwitchUserId);
            }
        }
    }

    internal static async Task<AdmissionResult?> TryHandleAdmissionWithRetryAsync(
        Func<CancellationToken, Task<AdmissionResult>> handle,
        ChatMessageEvent msg,
        ILogger log,
        CancellationToken ct,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        var delays = retryDelays ?? AdmissionRetryDelays;
        var maxAttempts = delays.Count + 1;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await handle(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Host shutdown always wins, even if the admission happened to
                // surface a different exception while cancellation was racing.
                ct.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = delays[attempt - 1];
                log.LogWarning(ex,
                    "Admission attempt {Attempt}/{MaxAttempts} failed for EventSub message {MessageId}; retrying in {Delay}",
                    attempt, maxAttempts, msg.MessageId, delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex,
                    "Admission failed for EventSub message {MessageId} after {Attempts} attempts; transport remains connected",
                    msg.MessageId, maxAttempts);
                return null;
            }
        }
    }

    internal sealed class ReconnectBackoffState
    {
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(60);

        public TimeSpan CurrentDelay { get; private set; } = InitialDelay;

        public void MarkSessionEstablished() => CurrentDelay = InitialDelay;

        public void AdvanceAfterFailure() =>
            CurrentDelay = TimeSpan.FromSeconds(
                Math.Min(CurrentDelay.TotalSeconds * 2, MaximumDelay.TotalSeconds));
    }

    private async Task CreateSubscriptionAsync(
        TwitchAuthService.ValidatedToken token, string sessionId, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("twitch");
        using var req = new HttpRequestMessage(HttpMethod.Post, HelixSubscriptions);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.AccessToken}");
        req.Headers.TryAddWithoutValidation("Client-Id", options.Value.ClientId);
        req.Content = JsonContent.Create(new
        {
            type = "channel.chat.message",
            version = "1",
            condition = new
            {
                // Watching our own channel as ourselves: minimum scope user:read:chat.
                broadcaster_user_id = token.UserId,
                user_id = token.UserId,
            },
            transport = new { method = "websocket", session_id = sessionId },
        });

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Creating channel.chat.message subscription failed ({(int)res.StatusCode}): {body}");
        logger.LogInformation("channel.chat.message subscription created");
    }

    /// <summary>Reads one complete text message, or null on keepalive timeout / close.</summary>
    /// <summary>Ceiling for one EventSub message; real ones are far smaller.</summary>
    private const int MaxIncomingMessageBytes = 1024 * 1024;

    /// <summary>
    /// `ClientWebSocket.ConnectAsync` has no timeout of its own — the token is
    /// the only bound, and the host's stopping token never fires mid-stream. A
    /// blackholed handshake (VPN flap, captive portal, a router dropping the
    /// flow) therefore hangs forever: the keepalive watchdog lives inside the
    /// receive loop, which is never reached, so the retry loop one frame up
    /// never regains control. The panel sits on "Connecting…" with no chat and
    /// no log line until the streamer restarts the app.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private static async Task ConnectWithTimeoutAsync(ClientWebSocket ws, Uri url, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConnectTimeout);
        try
        {
            await ws.ConnectAsync(url, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"EventSub WebSocket handshake did not complete within {ConnectTimeout.TotalSeconds:0}s.");
        }
    }

    /// <summary>
    /// True when a frame is a well-formed `session_welcome`. Used to confirm a
    /// reconnect handover before the working socket is dropped.
    /// </summary>
    private static bool IsSessionWelcome(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("message_type", out var type)
                && type.GetString() == "session_welcome"
                && doc.RootElement.TryGetProperty("payload", out var payload)
                && payload.TryGetProperty("session", out var session)
                && session.TryGetProperty("id", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string?> ReceiveMessageAsync(
        ClientWebSocket ws, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[16 * 1024];
        // Accumulate BYTES and decode once at the end. Decoding each fragment
        // separately corrupts any multi-byte character that straddles a frame
        // boundary — both halves become U+FFFD — which turns a chatter with an
        // emoji or a non-Latin display name into unparseable JSON.
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, timeoutCts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;

                // A hostile or broken peer must not be able to grow this without
                // bound; Twitch's own frames are orders of magnitude smaller.
                if (message.Length + result.Count > MaxIncomingMessageBytes)
                {
                    logger.LogWarning(
                        "Discarding an EventSub message larger than {Limit} bytes", MaxIncomingMessageBytes);
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // keepalive timeout, not shutdown
        }
    }
}

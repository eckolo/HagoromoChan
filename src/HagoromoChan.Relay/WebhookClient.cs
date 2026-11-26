using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HagoromoChan.Relay;

public sealed class WebhookDeliveryException(
    string reason,
    bool retryable,
    int? statusCode = null) : Exception($"Webhook delivery failed ({reason}).")
{
    public string Reason { get; } = reason;
    public bool Retryable { get; } = retryable;
    public int? StatusCode { get; } = statusCode;
}

public class WebhookClient : IDisposable
{
    private const int MaximumEventBytes = 256 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(2);
    private static readonly IPNetwork GlobalIpv6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] NonPublicNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("168.63.129.16/32"),
        IPNetwork.Parse("2001::/23"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20")
    ];

    private readonly HttpClient client;
    private readonly SemaphoreSlim verificationGate = new(1, 1);
    private readonly object rotationGate = new();
    private string? verifiedIdentity;
    private DateTimeOffset verifiedUntil;
    private SecretRotation? secretRotation;

    public WebhookClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectToPublicAddressAsync
        };
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public virtual async Task VerifyAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        ValidateSubscription(subscription);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            subscription.Id + "\n" + subscription.CallbackUrl + "\n" + subscription.Secret)));

        await verificationGate.WaitAsync(cancellationToken);
        try
        {
            if (identity == verifiedIdentity && DateTimeOffset.UtcNow < verifiedUntil)
                return;

            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var body = JsonSerializer.SerializeToUtf8Bytes(new { type = "verification", challenge });
            var response = await PostAsync(subscription, "verification_" + Guid.NewGuid().ToString("N"),
                body, readResponse: true, cancellationToken);

            try
            {
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("challenge", out var echoed) ||
                    echoed.ValueKind != JsonValueKind.String ||
                    !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(challenge), Encoding.UTF8.GetBytes(echoed.GetString()!)))
                {
                    throw new WebhookDeliveryException("challenge_failed", false);
                }
            }
            catch (JsonException)
            {
                throw new WebhookDeliveryException("challenge_failed", false);
            }

            verifiedIdentity = identity;
            verifiedUntil = DateTimeOffset.UtcNow.Add(VerificationLifetime);
        }
        finally
        {
            verificationGate.Release();
        }
    }

    public virtual async Task SendAsync(PendingDelivery delivery, CancellationToken cancellationToken)
    {
        var context = BuildInlineContext(delivery);
        var body = SerializeEvent(delivery, context);
        if (body.Length > MaximumEventBytes)
            body = SerializeEvent(delivery, []);
        if (body.Length > MaximumEventBytes)
            throw new WebhookDeliveryException("payload_too_large", false);

        await PostAsync(delivery.Subscription, delivery.EventId, body, readResponse: false, cancellationToken);
    }

    public virtual async Task SendWorkWakeAsync(Subscription subscription, string operationId,
        CancellationToken cancellationToken)
    {
        var eventId = "work_" + operationId;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId,
            name = McpHandler.WorkWakeEvent,
            timestamp = DateTimeOffset.UtcNow,
            data = new { channel_id = McpHandler.WorkWakeChannel, operation_id = operationId }
        });
        await PostAsync(subscription, eventId, body, readResponse: false, cancellationToken);
    }

    private static object[] BuildInlineContext(PendingDelivery delivery)
    {
        var messages = new List<object>();
        var remainingCharacters = 4096;
        foreach (var message in delivery.Context.Reverse())
        {
            if (message.Id == delivery.Message.Id)
                continue;
            if (remainingCharacters == 0)
                break;
            var length = Math.Min(message.Text.Length, remainingCharacters);
            if (length < message.Text.Length && length > 0 && char.IsHighSurrogate(message.Text[length - 1]))
                length--;
            messages.Add(new
            {
                message_id = message.Id,
                role = message.Role,
                text = message.Text[..length],
                in_reply_to = message.InReplyTo,
                created_at = message.CreatedAt,
                text_truncated = length < message.Text.Length
            });
            remainingCharacters -= length;
        }
        messages.Reverse();
        return messages.ToArray();
    }

    public void RememberSecretRotation(Subscription? previous, Subscription current)
    {
        if (previous is null || previous.Id != current.Id || previous.Secret == current.Secret)
            return;
        lock (rotationGate)
        {
            secretRotation = new SecretRotation(current.Id, current.Secret, previous.Secret,
                DateTimeOffset.UtcNow.AddMinutes(5));
        }
    }

    private static byte[] SerializeEvent(PendingDelivery delivery, object[] context) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId = delivery.EventId,
            name = McpHandler.EventName,
            timestamp = delivery.Message.CreatedAt,
            data = new
            {
                request_id = delivery.Message.Id,
                channel_id = McpHandler.SupportedChannel,
                text = delivery.Message.Text,
                in_reply_to = delivery.Message.InReplyTo,
                context
            },
            cursor = (string?)null
        });

    internal static void ValidateSubscription(Subscription subscription)
    {
        _ = ValidateCallbackUri(subscription.CallbackUrl);
        var key = DecodeSigningSecret(subscription.Secret);
        CryptographicOperations.ZeroMemory(key);
    }

    public static Uri ValidateCallbackUri(string callbackUrl)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
            uri.HostNameType == UriHostNameType.Unknown || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || uri.Port < 1 || uri.Port > 65535)
        {
            throw new WebhookDeliveryException("invalid_callback", false);
        }
        return uri;
    }

    private static byte[] DecodeSigningSecret(string secret)
    {
        if (!secret.StartsWith("whsec_", StringComparison.Ordinal))
            throw new WebhookDeliveryException("invalid_secret", false);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret[6..]);
        }
        catch (FormatException)
        {
            throw new WebhookDeliveryException("invalid_secret", false);
        }

        if (key.Length is < 24 or > 64 || Convert.ToBase64String(key) != secret[6..])
        {
            CryptographicOperations.ZeroMemory(key);
            throw new WebhookDeliveryException("invalid_secret", false);
        }
        return key;
    }

    public static string CreateSignature(string secret, string eventId, long timestamp, ReadOnlySpan<byte> body)
    {
        var key = DecodeSigningSecret(secret);
        try
        {
            using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            mac.AppendData(Encoding.UTF8.GetBytes(eventId + "." + timestamp.ToString(CultureInfo.InvariantCulture) + "."));
            mac.AppendData(body);
            return "v1," + Convert.ToBase64String(mac.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> PostAsync(Subscription subscription, string eventId, byte[] body,
        bool readResponse, CancellationToken cancellationToken)
    {
        var uri = ValidateCallbackUri(subscription.CallbackUrl);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("webhook-id", eventId);
        request.Headers.Add("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("webhook-signature", readResponse
            ? CreateSignature(subscription.Secret, eventId, timestamp, body)
            : DeliverySignatures(subscription, eventId, timestamp, body));
        request.Headers.Add("X-MCP-Subscription-Id", subscription.Id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var retryable = status is 408 or 429 || status >= 500;
                var reason = status switch
                {
                    410 => "callback_expired",
                    413 => "payload_too_large",
                    _ => "http_" + status.ToString(CultureInfo.InvariantCulture)
                };
                throw new WebhookDeliveryException(reason, retryable, status);
            }

            // Event delivery requires only a successful status. Never buffer its response body.
            if (!readResponse)
                return [];
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new WebhookDeliveryException("response_too_large", false);

            using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int bytesRead;
            while ((bytesRead = await responseStream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + bytesRead > MaximumResponseBytes)
                    throw new WebhookDeliveryException("response_too_large", false);
                buffer.Write(chunk, 0, bytesRead);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebhookDeliveryException("timeout", true);
        }
        catch (HttpRequestException exception)
        {
            if (exception.GetBaseException() is WebhookDeliveryException classified)
                throw classified;
            throw new WebhookDeliveryException("network_error", true);
        }
        catch (IOException)
        {
            throw new WebhookDeliveryException("network_error", true);
        }
    }

    private string DeliverySignatures(Subscription subscription, string eventId, long timestamp, byte[] body)
    {
        SecretRotation? rotation;
        lock (rotationGate)
        {
            if (secretRotation?.ExpiresAt <= DateTimeOffset.UtcNow)
                secretRotation = null;
            rotation = secretRotation;
        }
        if (rotation is null || rotation.SubscriptionId != subscription.Id)
            return CreateSignature(subscription.Secret, eventId, timestamp, body);
        return CreateSignature(rotation.CurrentSecret, eventId, timestamp, body) + " " +
            CreateSignature(rotation.PreviousSecret, eventId, timestamp, body);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (address.ScopeId != 0 || !GlobalIpv6.Contains(address)))
            return false;
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;
        return !NonPublicNetworks.Any(network => network.Contains(address));
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0)
            throw new WebhookDeliveryException("dns_no_addresses", true);
        if (addresses.Any(address => !IsPublicAddress(address)))
            throw new WebhookDeliveryException("non_public_callback", false);

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Connect to the exact address that was checked. HttpClient performs TLS for the original hostname.
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new WebhookDeliveryException("connection_failed", true);
    }

    public void Dispose()
    {
        client.Dispose();
        verificationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record SecretRotation(
        string SubscriptionId, string CurrentSecret, string PreviousSecret, DateTimeOffset ExpiresAt);
}

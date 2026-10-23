using System.Text.Json.Nodes;

namespace AlTamam.Contracts.Bridge;

/// <summary>
/// Canonical wire-format constants and hard limits for the C# &lt;-&gt; WebView2 UI bridge.
/// Every request envelope must carry exactly these members:
/// {requestId, protocolVersion, action, sessionNonce, payload}.
/// </summary>
public static class BridgeProtocol
{
    /// <summary>The only supported bridge protocol version.</summary>
    public const int ProtocolVersion = 1;

    // Envelope member names (canonical camelCase as serialized on the wire).
    public const string RequestIdField = "requestId";
    public const string ProtocolVersionField = "protocolVersion";
    public const string ActionField = "action";
    public const string SessionNonceField = "sessionNonce";
    public const string PayloadField = "payload";

    public const string SuccessField = "success";
    public const string DataField = "data";
    public const string ErrorCodeField = "errorCode";
    public const string UserMessageField = "userMessage";
    public const string RevisionField = "revision";
    public const string InvalidationEventsField = "invalidationEvents";

    /// <summary>Maximum accepted raw JSON message size in bytes (anti memory-exhaustion guard).</summary>
    public const int MaxRequestBytes = 256 * 1024;

    /// <summary>Maximum accepted requestId length.</summary>
    public const int MaxRequestIdLength = 64;

    /// <summary>Exact length of a canonical session nonce (32 hex chars).</summary>
    public const int SessionNonceLength = 32;

    /// <summary>Client-side correlation timeout recommended to the JS layer (ms).</summary>
    public const int DefaultRequestTimeoutMs = 15_000;

    /// <summary>True when <paramref name="text"/> has the canonical session-nonce format (fixed-length hex).</summary>
    public static bool IsValidSessionNonceFormat(string? text)
    {
        if (text is not { Length: SessionNonceLength }) return false;
        foreach (char c in text)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }
}

/// <summary>Structured failure raised while parsing/validating a bridge envelope.</summary>
public sealed class BridgeProtocolException : Exception
{
    public BridgeProtocolException(string errorCode, string userMessage)
        : base(userMessage)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
    }

    public string ErrorCode { get; }
    public string UserMessage { get; }
}

/// <summary>
/// Strongly-typed parser for the canonical <see cref="BridgeRequest"/> envelope.
/// Performs schema validation (exact required members, types, sizes) before any handler runs.
/// </summary>
public static class BridgeRequestParser
{
    /// <summary>Parses and validates one canonical request envelope from raw JSON text.</summary>
    /// <exception cref="BridgeProtocolException">On malformed input, wrong protocol version, oversized message or bad nonce format.</exception>
    public static BridgeRequest Parse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            Throw(BridgeErrorCodes.ValidationError, "الطلب فارغ أو يحتوي على مسافات فقط.");
        if (System.Text.Encoding.UTF8.GetByteCount(rawJson) > BridgeProtocol.MaxRequestBytes)
            Throw(BridgeErrorCodes.ValidationError, "حجم الطلب يتجاوز الحد المسموح.");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson, documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = false,
                MaxDepth = 32
            });
        }
        catch (System.Text.Json.JsonException)
        {
            Throw(BridgeErrorCodes.ValidationError, "الطلب غير صالح: ترميز JSON غير سليم.");
            return null!; // unreachable
        }

        if (root is not JsonObject obj)
            return Throw<BridgeRequest>(BridgeErrorCodes.ValidationError, "الطلب غير صالح: يجب أن يكون كائن JSON.");

        string? requestId = ReadString(obj, BridgeProtocol.RequestIdField, required: true);
        if (string.IsNullOrEmpty(requestId) || requestId.Length > BridgeProtocol.MaxRequestIdLength)
            Throw(BridgeErrorCodes.ValidationError, "معرّف الطلب مفقود أو خارج الحدود.");

        if (!obj.TryGetPropertyValue(BridgeProtocol.ProtocolVersionField, out JsonNode? protoNode) || protoNode is null)
            Throw(BridgeErrorCodes.ValidationError, "إصدار البروتوكول مفقود.");
        int version;
        try { version = protoNode!.GetValue<int>(); }
        catch
        {
            return Throw<BridgeRequest>(BridgeErrorCodes.ValidationError, "إصدار البروتوكول غير صالح.");
        }
        if (version != BridgeProtocol.ProtocolVersion)
            Throw(BridgeErrorCodes.ValidationError,
                $"إصدار جسر غير مدعوم ({version}). الإصدار المعتمد: {BridgeProtocol.ProtocolVersion}.");

        string? action = ReadString(obj, BridgeProtocol.ActionField, required: true);
        if (string.IsNullOrWhiteSpace(action))
            Throw(BridgeErrorCodes.ValidationError, "الإجراء مفقود.");

        string? nonce = ReadString(obj, BridgeProtocol.SessionNonceField, required: true);
        if (!BridgeProtocol.IsValidSessionNonceFormat(nonce))
            Throw(BridgeErrorCodes.ValidationError, "رمز الجلسة مفقود أو بصيغة غير سليمة.");

        if (!obj.ContainsKey(BridgeProtocol.PayloadField))
            Throw(BridgeErrorCodes.ValidationError, "حمولة الطلب مفقودة.");
        JsonObject payload = obj[BridgeProtocol.PayloadField] switch
        {
            JsonObject p => p,
            null => new JsonObject(),
            _ => Throw<JsonObject>(BridgeErrorCodes.ValidationError, "يجب أن تكون الحمولة كائن JSON.")
        };

        return new BridgeRequest(requestId!, version, action!, nonce!, payload);
    }

    private static string? ReadString(JsonObject obj, string name, bool required)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
            return required ? null : string.Empty;
        try { return node.GetValue<string>(); }
        catch
        {
            Throw(BridgeErrorCodes.ValidationError, $"الحقل '{name}' يجب أن يكون نصاً.");
            return null;
        }
    }

    private static void Throw(string code, string message) => Throw<object?>(code, message);

    private static T Throw<T>(string code, string message) => throw new BridgeProtocolException(code, message);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Section 08: backwards-compatible, truthful versioned bridge wire contract.</summary>
public sealed class Section08ResponseEnvelopeTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Fact]
    public void FailedValidation_WireHasStableCodeArabicMessageAndFieldErrors_ButNoData()
    {
        var reply = new BridgeResponse("REQ-SEC08-1", false, null,
            BridgeErrorCodes.ValidationError, "معرف العملية غير صالح.")
        {
            ValidationErrors = new JsonObject { ["operationId"] = "معرف العملية غير صالح." }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(reply, Wire));
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("ValidationError", root.GetProperty("code").GetString());
        Assert.Equal("معرف العملية غير صالح.", root.GetProperty("messageAr").GetString());
        Assert.Equal("ValidationError", root.GetProperty("errorCode").GetString());
        Assert.Equal("معرف العملية غير صالح.", root.GetProperty("userMessage").GetString());
        Assert.True(root.GetProperty("validationErrors").TryGetProperty("operationId", out _));
        Assert.False(root.TryGetProperty("data", out _));
        Assert.Equal("REQ-SEC08-1", root.GetProperty("requestId").GetString());
    }

    [Fact]
    public void Conflict_WireContainsBothRevisionsAndReason()
    {
        var reply = new BridgeResponse("REQ-SEC08-2", false, null,
            BridgeErrorCodes.ConflictStale, "تغيرت البيانات.")
        {
            ExpectedVersion = 7,
            ActualVersion = 5,
            ConflictInfo = new JsonObject { ["reason"] = "OptimisticConcurrency" }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(reply, Wire));
        var root = doc.RootElement;
        Assert.Equal("ConflictStale", root.GetProperty("code").GetString());
        Assert.Equal(7L, root.GetProperty("expectedVersion").GetInt64());
        Assert.Equal(5L, root.GetProperty("actualVersion").GetInt64());
        Assert.Equal("OptimisticConcurrency",
            root.GetProperty("conflictInfo").GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("data", out _));
    }

    [Fact]
    public void SuccessfulRead_DoesNotInventAnAuditEventOrConflict()
    {
        var reply = new BridgeResponse("REQ-SEC08-3", true,
            new JsonObject { ["count"] = 3 });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(reply, Wire));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("OK", root.GetProperty("code").GetString());
        Assert.Equal(3, root.GetProperty("data").GetProperty("count").GetInt32());
        Assert.Null(reply.AuditEventId);
        Assert.Null(reply.ConflictInfo);
        Assert.Null(reply.ValidationErrors);
    }

    [Theory]
    [InlineData("with whitespace")]
    [InlineData("معرّف")]
    [InlineData("../bad-id")]
    public void Parser_RejectsUnsafeRequestCorrelationIds(string invalid)
    {
        var raw = new JsonObject {
            ["requestId"] = invalid,
            ["protocolVersion"] = 1,
            ["action"] = "GetCurrentSession",
            ["sessionNonce"] = new string('a', 32),
            ["payload"] = new JsonObject()
        }.ToJsonString();
        var thrown = Assert.Throws<BridgeProtocolException>(() => BridgeRequestParser.Parse(raw));
        Assert.Equal(BridgeErrorCodes.ValidationError, thrown.ErrorCode);
    }
}

using System;
using System.Linq;
using Xunit;

namespace Snapnedit.Tests;

/// <summary>
/// The signature verifier, against the fixed vector every snapnedit SDK is held
/// to (<c>test/conformance/scenarios.json</c> → <c>webhookVector</c>).
/// </summary>
public sealed class WebhookTests
{
    internal const string Secret = "whsec_conformance_fixed_test_vector";
    internal const long Timestamp = 1700000000;

    internal const string RawBody =
        "{\"id\":\"whd_00000000-0000-4000-8000-000000000001\",\"type\":\"job.succeeded\",\"created\":1700000000," +
        "\"data\":{\"jobId\":\"9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d\",\"operation\":\"remove-background\"," +
        "\"status\":\"succeeded\",\"outputAssetId\":\"3f2504e0-4f89-41d3-9a0c-0305e82c3301\"," +
        "\"download\":\"https://snapnedit.com/_local/results/abc.png?exp=1700003600&sig=deadbeef\"," +
        "\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}}";

    internal const string Signature = "8a587f5869207b7da3be52c9d2695b0201c442c27cc37d2176551349e6461d90";
    internal const string Header = "t=1700000000,v1=" + Signature;

    [Fact]
    public void The_recomputed_mac_matches_the_published_vector() =>
        Assert.Equal(Signature, SnapneditWebhooks.ComputeSignature(Timestamp, RawBody, Secret));

    [Fact]
    public void The_correct_header_verifies() =>
        Assert.True(SnapneditWebhooks.Verify(RawBody, Header, Secret));

    [Theory]
    [InlineData("t=1700000000,v1=8a587f5869207b7da3be52c9d2695b0201c442c27cc37d2176551349e6461d91")]
    [InlineData("not a signature header")]
    [InlineData("v1=8a587f5869207b7da3be52c9d2695b0201c442c27cc37d2176551349e6461d90")]
    [InlineData("t=1700000000")]
    [InlineData("")]
    public void A_bad_or_malformed_header_returns_false_and_never_throws(string header) =>
        Assert.False(SnapneditWebhooks.Verify(RawBody, header, Secret));

    [Fact]
    public void A_changed_secret_fails() =>
        Assert.False(SnapneditWebhooks.Verify(RawBody, Header, "wrong secret"));

    [Fact]
    public void One_extra_byte_of_body_fails_because_the_raw_bytes_are_what_is_signed() =>
        Assert.False(SnapneditWebhooks.Verify(RawBody + " ", Header, Secret));

    [Fact]
    public void An_uppercase_v1_still_verifies() =>
        Assert.True(SnapneditWebhooks.Verify(RawBody, "t=1700000000,v1=" + Signature.ToUpperInvariant(), Secret));

    [Fact]
    public void Any_v1_in_a_rotation_window_accepts_the_delivery() =>
        Assert.True(SnapneditWebhooks.Verify(
            RawBody,
            "t=1700000000,v1=" + new string('0', 64) + ",v1=" + Signature + ",extra=ignored",
            Secret));

    [Fact]
    public void A_fresh_timestamp_inside_the_window_verifies() =>
        Assert.True(SnapneditWebhooks.Verify(
            RawBody,
            Header,
            Secret,
            new WebhookVerifyOptions { ToleranceSeconds = 300, NowUnixSeconds = Timestamp + 10 }));

    [Fact]
    public void A_stale_timestamp_fails_even_though_the_mac_is_correct() =>
        Assert.False(SnapneditWebhooks.Verify(
            RawBody,
            Header,
            Secret,
            new WebhookVerifyOptions { ToleranceSeconds = 300, NowUnixSeconds = Timestamp + 3600 }));

    [Fact]
    public void ConstructEvent_verifies_then_parses()
    {
        var evt = SnapneditWebhooks.ConstructEvent(RawBody, Header, Secret);

        Assert.Equal("whd_00000000-0000-4000-8000-000000000001", evt.Id);
        Assert.Equal(WebhookEventType.JobSucceeded, evt.Type);
        Assert.Equal(1700000000L, evt.Created);
        Assert.Equal("9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d", evt.Data.JobId);
        Assert.Equal(Operations.RemoveBackground, evt.Data.Operation);
        Assert.Equal(JobState.Succeeded, evt.Data.State);
        Assert.Equal("3f2504e0-4f89-41d3-9a0c-0305e82c3301", evt.Data.OutputAssetId);
        Assert.Equal(JobInputKind.Asset, evt.Data.InputKind);
        Assert.Null(evt.Data.Destination);
        Assert.Null(evt.Data.Delivery);
    }

    [Fact]
    public void ConstructEvent_refuses_a_bad_signature_with_unauthorized()
    {
        var error = Assert.Throws<SnapneditException>(
            () => SnapneditWebhooks.ConstructEvent(RawBody, "t=1,v1=deadbeef", Secret));

        Assert.Equal(ErrorCode.Unauthorized, error.Code);
    }

    [Fact]
    public void A_failed_job_event_carries_its_error_code()
    {
        const string Body = "{\"id\":\"whd_1\",\"type\":\"job.failed\",\"created\":1,\"data\":{\"jobId\":\"j\"," +
                            "\"operation\":\"remove-background\",\"status\":\"failed\",\"errorCode\":\"input_fetch_failed\"," +
                            "\"message\":\"nope\",\"input\":{\"kind\":\"url\"},\"destination\":null,\"delivery\":null}}";

        var evt = SnapneditWebhooks.Parse(Body);

        Assert.Equal(WebhookEventType.JobFailed, evt.Type);
        Assert.Equal(JobState.Failed, evt.Data.State);
        Assert.Equal(ErrorCode.InputFetchFailed, evt.Data.ErrorCode);
        Assert.Equal(JobInputKind.Url, evt.Data.InputKind);
    }

    [Fact]
    public void An_unknown_event_type_parses_as_a_forward_compatible_no_op()
    {
        const string Body = "{\"id\":\"whd_2\",\"type\":\"job.something-new\",\"created\":1,\"data\":{\"jobId\":\"j\"," +
                            "\"operation\":\"upscale\",\"status\":\"succeeded\",\"outputAssetId\":\"o\"," +
                            "\"input\":{\"kind\":\"asset\"},\"destination\":{\"type\":\"saved\",\"id\":\"d\",\"name\":\"n\"}," +
                            "\"delivery\":{\"status\":\"failed\",\"attempts\":3,\"error\":\"403\"}}}";

        var evt = SnapneditWebhooks.Parse(Body);

        Assert.Equal(WebhookEventType.Unknown, evt.Type);
        Assert.Equal("job.something-new", evt.RawType);
        // succeeded + a FAILED delivery is a real combination, not a contradiction.
        Assert.Equal(JobState.Succeeded, evt.Data.State);
        Assert.Equal(JobDeliveryStatus.Failed, evt.Data.Delivery!.Status);
        Assert.Equal("d", evt.Data.Destination!.Id);
    }

    [Fact]
    public void Header_names_match_what_the_server_sends()
    {
        Assert.Equal("X-Snapnedit-Signature", SnapneditWebhooks.SignatureHeader);
        Assert.Equal("X-Snapnedit-Event", SnapneditWebhooks.EventHeader);
        Assert.Equal("X-Snapnedit-Delivery", SnapneditWebhooks.DeliveryHeader);
    }
}

/// <summary>The operation catalog constants, and the error-code mapping.</summary>
public sealed class CatalogTests
{
    [Fact]
    public void There_are_seventeen_operations() => Assert.Equal(17, Operations.All.Count);

    [Fact]
    public void Exactly_three_operations_require_a_mask() =>
        Assert.Equal(
            new[] { "generative-fill", "magic-eraser", "remove-watermark" },
            Operations.RequiringMask.OrderBy(id => id, StringComparer.Ordinal).ToArray());

    [Fact]
    public void Every_operation_has_a_published_credit_cost() =>
        Assert.All(Operations.All, id => Assert.True(Operations.CreditCosts.ContainsKey(id)));

    [Fact]
    public void Resize_image_is_the_free_one() => Assert.Equal(0, Operations.CreditCosts[Operations.ResizeImage]);

    [Fact]
    public void Every_error_code_round_trips_through_its_wire_spelling() =>
        Assert.All(ErrorCodes.All, code => Assert.Equal(code, ErrorCodes.Parse(ErrorCodes.ToWire(code))));

    [Fact]
    public void An_unknown_wire_code_degrades_to_internal() =>
        Assert.Equal(ErrorCode.Internal, ErrorCodes.Parse("brand_new_code"));

    [Fact]
    public void Every_typed_params_record_names_an_operation_that_exists() =>
        Assert.All(
            new IOperationParams[]
            {
                new RemoveBackgroundParams(),
                new UpscaleParams(),
                new UnblurParams(),
                new ColorizeParams(),
                new StyleTransferParams(),
                new RetouchParams(),
                new BeautifyParams(),
                new MagicEraserParams(),
                new GenerativeFillParams(),
                new RemoveWatermarkParams(),
                new AiDenoiseParams(),
                new ReplaceSkyParams(),
                new RelightParams(),
                new ReplaceBackgroundParams(),
                new StripMetadataParams(),
                new AutoRemoveWatermarkParams(),
                new ResizeImageParams(),
            },
            p => Assert.Contains(p.Operation, Operations.All));

    [Fact]
    public void Unset_typed_params_send_nothing_so_the_api_applies_its_own_defaults() =>
        Assert.Empty(new UpscaleParams().ToParams());

    [Fact]
    public void Every_export_extension_has_a_canonical_content_type() =>
        Assert.All(
            Enum.GetValues(typeof(DestinationExportExt)).Cast<DestinationExportExt>(),
            ext => Assert.NotEmpty(DestinationExportExtensions.CanonicalContentType(ext)));
}

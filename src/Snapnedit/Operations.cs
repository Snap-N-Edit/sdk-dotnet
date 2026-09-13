using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>
/// The 17 operation ids the api serves, as constants — so a typo is a compile
/// error rather than a 400 at runtime.
/// </summary>
/// <remarks>
/// The authoritative list is <c>GET /operations</c> (see
/// <see cref="SnapneditClient.ListOperationsAsync"/>), which also reports each
/// operation's accepted mime types, maximum input dimension, JSON-Schema for
/// its params and whether it requires a mask. Three operations
/// (<see cref="GenerativeFill"/>, <see cref="Relight"/>,
/// <see cref="AutoRemoveWatermark"/>) may be deferred on a given deployment.
/// </remarks>
public static class Operations
{
    /// <summary>Cut the subject out of its background. 1 credit.</summary>
    public const string RemoveBackground = "remove-background";

    /// <summary>Enlarge an image 2x or 4x. 2 credits.</summary>
    public const string Upscale = "upscale";

    /// <summary>Sharpen a blurry photo. 1 credit.</summary>
    public const string Unblur = "unblur";

    /// <summary>Colorize a black-and-white photo. 1 credit.</summary>
    public const string Colorize = "colorize";

    /// <summary>Apply an artistic style. 2 credits.</summary>
    public const string StyleTransfer = "style-transfer";

    /// <summary>General photo retouch. 1 credit.</summary>
    public const string Retouch = "retouch";

    /// <summary>Portrait beautify. 1 credit.</summary>
    public const string Beautify = "beautify";

    /// <summary>Erase what a mask covers. Requires <c>maskAssetId</c>. 2 credits.</summary>
    public const string MagicEraser = "magic-eraser";

    /// <summary>Fill a masked region from a prompt. Requires <c>maskAssetId</c>. 3 credits.</summary>
    public const string GenerativeFill = "generative-fill";

    /// <summary>Remove a watermark a mask covers. Requires <c>maskAssetId</c>. 2 credits.</summary>
    public const string RemoveWatermark = "remove-watermark";

    /// <summary>Denoise. 1 credit.</summary>
    public const string AiDenoise = "ai-denoise";

    /// <summary>Replace the sky. 2 credits.</summary>
    public const string ReplaceSky = "replace-sky";

    /// <summary>Relight the subject. 2 credits.</summary>
    public const string Relight = "relight";

    /// <summary>Replace the background with a preset. 2 credits.</summary>
    public const string ReplaceBackground = "replace-background";

    /// <summary>Strip EXIF and other metadata. 1 credit.</summary>
    public const string StripMetadata = "strip-metadata";

    /// <summary>Detect and remove a watermark without a mask. 2 credits.</summary>
    public const string AutoRemoveWatermark = "auto-remove-watermark";

    /// <summary>Resize / re-encode. Free — 0 credits.</summary>
    public const string ResizeImage = "resize-image";

    /// <summary>Every operation id, in the order <c>GET /operations</c> returns them.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        RemoveBackground,
        Upscale,
        Unblur,
        Colorize,
        StyleTransfer,
        Retouch,
        Beautify,
        MagicEraser,
        GenerativeFill,
        RemoveWatermark,
        AiDenoise,
        ReplaceSky,
        Relight,
        ReplaceBackground,
        StripMetadata,
        AutoRemoveWatermark,
        ResizeImage,
    };

    /// <summary>The three operations that will not run without <c>params.maskAssetId</c>.</summary>
    public static readonly IReadOnlyList<string> RequiringMask = new[]
    {
        MagicEraser,
        GenerativeFill,
        RemoveWatermark,
    };

    /// <summary>
    /// The credit cost of each operation, as published on the OpenAPI document
    /// (<c>x-credit-cost</c>). A cost of <c>0</c> is free; a cache hit is never
    /// billed and a terminal failure is refunded.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> CreditCosts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [RemoveBackground] = 1,
        [Upscale] = 2,
        [Unblur] = 1,
        [Colorize] = 1,
        [StyleTransfer] = 2,
        [Retouch] = 1,
        [Beautify] = 1,
        [MagicEraser] = 2,
        [GenerativeFill] = 3,
        [RemoveWatermark] = 2,
        [AiDenoise] = 1,
        [ReplaceSky] = 2,
        [Relight] = 2,
        [ReplaceBackground] = 2,
        [StripMetadata] = 1,
        [AutoRemoveWatermark] = 2,
        [ResizeImage] = 0,
    };
}

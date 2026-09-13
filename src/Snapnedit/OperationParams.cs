using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>
/// A strongly-typed params object for one operation. Pass it as
/// <see cref="CreateJobOptions.TypedParams"/> (or
/// <see cref="CreateJobOptions.TypedParams"/>) instead of hand-rolling a
/// dictionary: the api validates params strictly, and rejects both an unknown
/// key and an out-of-range value with <see cref="ErrorCode.InvalidInput"/>.
/// </summary>
public interface IOperationParams
{
    /// <summary>The operation these params belong to, e.g. <see cref="Operations.Upscale"/>.</summary>
    string Operation { get; }

    /// <summary>Renders the params as the wire dictionary <c>POST /jobs</c> expects. Unset values are omitted so the api applies its own defaults.</summary>
    /// <returns>A dictionary with only the values that were set.</returns>
    IReadOnlyDictionary<string, object?> ToParams();
}

/// <summary>Helpers shared by the typed params records.</summary>
internal static class ParamBuilder
{
    internal static Dictionary<string, object?> New() => new Dictionary<string, object?>(StringComparer.Ordinal);

    internal static void Put(Dictionary<string, object?> into, string key, object? value)
    {
        if (value is not null)
        {
            into[key] = value;
        }
    }
}

/// <summary>How <see cref="UpscaleParams"/> enlarges an image.</summary>
public enum UpscaleFactor
{
    /// <summary>2x — the api default. Wire value <c>"2"</c>.</summary>
    X2 = 0,

    /// <summary>4x. Wire value <c>"4"</c>.</summary>
    X4 = 1,
}

/// <summary>Strength of <see cref="AiDenoiseParams"/>.</summary>
public enum DenoiseStrength
{
    /// <summary>Wire value <c>"0.25"</c>.</summary>
    Quarter = 0,

    /// <summary>Wire value <c>"0.5"</c>.</summary>
    Half = 1,

    /// <summary>Wire value <c>"0.75"</c>.</summary>
    ThreeQuarters = 2,

    /// <summary>Wire value <c>"1"</c> — the api default.</summary>
    Full = 3,
}

/// <summary>Amount of <see cref="BeautifyParams"/> smoothing.</summary>
public enum BeautifyAmount
{
    /// <summary>Wire value <c>"0.3"</c>.</summary>
    Subtle = 0,

    /// <summary>Wire value <c>"0.6"</c> — the api default.</summary>
    Balanced = 1,

    /// <summary>Wire value <c>"0.9"</c>.</summary>
    Strong = 2,

    /// <summary>Wire value <c>"1"</c>.</summary>
    Max = 3,
}

/// <summary>Strength of <see cref="AutoRemoveWatermarkParams"/>.</summary>
public enum WatermarkStrength
{
    /// <summary>Wire value <c>"low"</c>.</summary>
    Low = 0,

    /// <summary>Wire value <c>"medium"</c> — the api default.</summary>
    Medium = 1,

    /// <summary>Wire value <c>"high"</c>.</summary>
    High = 2,
}

/// <summary>Quality/latency trade-off for <see cref="GenerativeFillParams"/>.</summary>
public enum GenerativeFillMode
{
    /// <summary>Wire value <c>"fast"</c>.</summary>
    Fast = 0,

    /// <summary>Wire value <c>"quality"</c> — the api default.</summary>
    Quality = 1,
}

/// <summary>Light direction for <see cref="RelightParams"/>.</summary>
public enum RelightDirection
{
    /// <summary>Wire value <c>"left"</c>.</summary>
    Left = 0,

    /// <summary>Wire value <c>"right"</c>.</summary>
    Right = 1,

    /// <summary>Wire value <c>"front"</c> — the api default.</summary>
    Front = 2,

    /// <summary>Wire value <c>"top"</c>.</summary>
    Top = 3,

    /// <summary>Wire value <c>"backlit"</c>.</summary>
    Backlit = 4,
}

/// <summary>Preset background for <see cref="ReplaceBackgroundParams"/>.</summary>
public enum BackgroundPreset
{
    /// <summary>Wire value <c>"white"</c> — the api default.</summary>
    White = 0,

    /// <summary>Wire value <c>"black"</c>.</summary>
    Black = 1,

    /// <summary>Wire value <c>"studio-grey"</c>.</summary>
    StudioGrey = 2,

    /// <summary>Wire value <c>"studio-blue"</c>.</summary>
    StudioBlue = 3,

    /// <summary>Wire value <c>"sunset"</c>.</summary>
    Sunset = 4,

    /// <summary>Wire value <c>"ocean"</c>.</summary>
    Ocean = 5,

    /// <summary>Wire value <c>"lavender"</c>.</summary>
    Lavender = 6,
}

/// <summary>Preset sky for <see cref="ReplaceSkyParams"/>.</summary>
public enum SkyPreset
{
    /// <summary>Wire value <c>"blue-sky"</c> — the api default.</summary>
    BlueSky = 0,

    /// <summary>Wire value <c>"sunset"</c>.</summary>
    Sunset = 1,

    /// <summary>Wire value <c>"dramatic-clouds"</c>.</summary>
    DramaticClouds = 2,

    /// <summary>Wire value <c>"golden-hour"</c>.</summary>
    GoldenHour = 3,

    /// <summary>Wire value <c>"night"</c>.</summary>
    Night = 4,

    /// <summary>Wire value <c>"overcast"</c>.</summary>
    Overcast = 5,
}

/// <summary>Artistic style for <see cref="StyleTransferParams"/>.</summary>
public enum TransferStyle
{
    /// <summary>Wire value <c>"vivid"</c> — the api default.</summary>
    Vivid = 0,

    /// <summary>Wire value <c>"pastel"</c>.</summary>
    Pastel = 1,

    /// <summary>Wire value <c>"mosaic"</c>.</summary>
    Mosaic = 2,

    /// <summary>Wire value <c>"storm"</c>.</summary>
    Storm = 3,
}

/// <summary>How <see cref="ResizeImageParams"/> fits the image into the requested box.</summary>
public enum ResizeFit
{
    /// <summary>Contain, preserving aspect ratio — the api default. Wire value <c>"inside"</c>.</summary>
    Inside = 0,

    /// <summary>Cover and crop. Wire value <c>"cover"</c>.</summary>
    Cover = 1,

    /// <summary>Stretch to the exact box. Wire value <c>"fill"</c>.</summary>
    Fill = 2,
}

/// <summary>Output encoding for <see cref="ResizeImageParams"/>.</summary>
public enum ResizeFormat
{
    /// <summary>Wire value <c>"png"</c> — the api default.</summary>
    Png = 0,

    /// <summary>Wire value <c>"jpeg"</c>.</summary>
    Jpeg = 1,

    /// <summary>Wire value <c>"webp"</c>.</summary>
    Webp = 2,
}

/// <summary>Wire spellings for the params enums.</summary>
internal static class ParamEnum
{
    internal static string Wire(UpscaleFactor value) => value == UpscaleFactor.X4 ? "4" : "2";

    internal static string Wire(DenoiseStrength value) => value switch
    {
        DenoiseStrength.Quarter => "0.25",
        DenoiseStrength.Half => "0.5",
        DenoiseStrength.ThreeQuarters => "0.75",
        _ => "1",
    };

    internal static string Wire(BeautifyAmount value) => value switch
    {
        BeautifyAmount.Subtle => "0.3",
        BeautifyAmount.Strong => "0.9",
        BeautifyAmount.Max => "1",
        _ => "0.6",
    };

    internal static string Wire(WatermarkStrength value) => value switch
    {
        WatermarkStrength.Low => "low",
        WatermarkStrength.High => "high",
        _ => "medium",
    };

    internal static string Wire(GenerativeFillMode value) => value == GenerativeFillMode.Fast ? "fast" : "quality";

    internal static string Wire(RelightDirection value) => value switch
    {
        RelightDirection.Left => "left",
        RelightDirection.Right => "right",
        RelightDirection.Top => "top",
        RelightDirection.Backlit => "backlit",
        _ => "front",
    };

    internal static string Wire(BackgroundPreset value) => value switch
    {
        BackgroundPreset.Black => "black",
        BackgroundPreset.StudioGrey => "studio-grey",
        BackgroundPreset.StudioBlue => "studio-blue",
        BackgroundPreset.Sunset => "sunset",
        BackgroundPreset.Ocean => "ocean",
        BackgroundPreset.Lavender => "lavender",
        _ => "white",
    };

    internal static string Wire(SkyPreset value) => value switch
    {
        SkyPreset.Sunset => "sunset",
        SkyPreset.DramaticClouds => "dramatic-clouds",
        SkyPreset.GoldenHour => "golden-hour",
        SkyPreset.Night => "night",
        SkyPreset.Overcast => "overcast",
        _ => "blue-sky",
    };

    internal static string Wire(TransferStyle value) => value switch
    {
        TransferStyle.Pastel => "pastel",
        TransferStyle.Mosaic => "mosaic",
        TransferStyle.Storm => "storm",
        _ => "vivid",
    };

    internal static string Wire(ResizeFit value) => value switch
    {
        ResizeFit.Cover => "cover",
        ResizeFit.Fill => "fill",
        _ => "inside",
    };

    internal static string Wire(ResizeFormat value) => value switch
    {
        ResizeFormat.Jpeg => "jpeg",
        ResizeFormat.Webp => "webp",
        _ => "png",
    };
}

/// <summary>Params for <see cref="Operations.RemoveBackground"/>. The operation takes none.</summary>
public sealed record RemoveBackgroundParams : IOperationParams
{
    /// <inheritdoc />
    public string Operation => Operations.RemoveBackground;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams() => ParamBuilder.New();
}

/// <summary>Params for <see cref="Operations.Upscale"/>.</summary>
public sealed record UpscaleParams : IOperationParams
{
    /// <summary>How much to enlarge. Omit for the api default (2x).</summary>
    public UpscaleFactor? Factor { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.Upscale;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Factor.HasValue)
        {
            into["factor"] = ParamEnum.Wire(Factor.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.Unblur"/>. The operation takes none.</summary>
public sealed record UnblurParams : IOperationParams
{
    /// <inheritdoc />
    public string Operation => Operations.Unblur;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams() => ParamBuilder.New();
}

/// <summary>Params for <see cref="Operations.Colorize"/>. The operation takes none.</summary>
public sealed record ColorizeParams : IOperationParams
{
    /// <inheritdoc />
    public string Operation => Operations.Colorize;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams() => ParamBuilder.New();
}

/// <summary>Params for <see cref="Operations.StyleTransfer"/>.</summary>
public sealed record StyleTransferParams : IOperationParams
{
    /// <summary>Which style to apply. Omit for the api default (vivid).</summary>
    public TransferStyle? Style { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.StyleTransfer;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Style.HasValue)
        {
            into["style"] = ParamEnum.Wire(Style.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.Retouch"/>. The operation takes none.</summary>
public sealed record RetouchParams : IOperationParams
{
    /// <inheritdoc />
    public string Operation => Operations.Retouch;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams() => ParamBuilder.New();
}

/// <summary>Params for <see cref="Operations.Beautify"/>.</summary>
public sealed record BeautifyParams : IOperationParams
{
    /// <summary>How much to smooth. Omit for the api default (balanced).</summary>
    public BeautifyAmount? Amount { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.Beautify;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Amount.HasValue)
        {
            into["amount"] = ParamEnum.Wire(Amount.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.MagicEraser"/>. Requires a mask.</summary>
public sealed record MagicEraserParams : IOperationParams
{
    /// <summary>
    /// Asset id of the mask image. Leave unset and pass the mask bytes as
    /// <see cref="RunOptions.Mask"/> instead — the client uploads it and fills
    /// this in.
    /// </summary>
    public string? MaskAssetId { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.MagicEraser;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        ParamBuilder.Put(into, "maskAssetId", MaskAssetId);
        return into;
    }
}

/// <summary>Params for <see cref="Operations.GenerativeFill"/>. Requires a mask and a prompt.</summary>
public sealed record GenerativeFillParams : IOperationParams
{
    /// <summary>What to paint into the masked region. Required by the api.</summary>
    public string? Prompt { get; init; }

    /// <summary>Quality/latency trade-off. Omit for the api default (quality).</summary>
    public GenerativeFillMode? Mode { get; init; }

    /// <summary>Asset id of the mask image, or leave unset and pass <see cref="RunOptions.Mask"/>.</summary>
    public string? MaskAssetId { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.GenerativeFill;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        ParamBuilder.Put(into, "prompt", Prompt);
        if (Mode.HasValue)
        {
            into["mode"] = ParamEnum.Wire(Mode.Value);
        }

        ParamBuilder.Put(into, "maskAssetId", MaskAssetId);
        return into;
    }
}

/// <summary>Params for <see cref="Operations.RemoveWatermark"/>. Requires a mask.</summary>
public sealed record RemoveWatermarkParams : IOperationParams
{
    /// <summary>Asset id of the mask image, or leave unset and pass <see cref="RunOptions.Mask"/>.</summary>
    public string? MaskAssetId { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.RemoveWatermark;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        ParamBuilder.Put(into, "maskAssetId", MaskAssetId);
        return into;
    }
}

/// <summary>Params for <see cref="Operations.AiDenoise"/>.</summary>
public sealed record AiDenoiseParams : IOperationParams
{
    /// <summary>How aggressively to denoise. Omit for the api default (full).</summary>
    public DenoiseStrength? Strength { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.AiDenoise;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Strength.HasValue)
        {
            into["strength"] = ParamEnum.Wire(Strength.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.ReplaceSky"/>.</summary>
public sealed record ReplaceSkyParams : IOperationParams
{
    /// <summary>Which sky to composite in. Omit for the api default (blue sky).</summary>
    public SkyPreset? Sky { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.ReplaceSky;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Sky.HasValue)
        {
            into["sky"] = ParamEnum.Wire(Sky.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.Relight"/>.</summary>
public sealed record RelightParams : IOperationParams
{
    /// <summary>Where the light comes from. Omit for the api default (front).</summary>
    public RelightDirection? Direction { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.Relight;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Direction.HasValue)
        {
            into["direction"] = ParamEnum.Wire(Direction.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.ReplaceBackground"/>.</summary>
public sealed record ReplaceBackgroundParams : IOperationParams
{
    /// <summary>Which preset background to composite in. Omit for the api default (white).</summary>
    public BackgroundPreset? Background { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.ReplaceBackground;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Background.HasValue)
        {
            into["background"] = ParamEnum.Wire(Background.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.StripMetadata"/>. The operation takes none.</summary>
public sealed record StripMetadataParams : IOperationParams
{
    /// <inheritdoc />
    public string Operation => Operations.StripMetadata;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams() => ParamBuilder.New();
}

/// <summary>Params for <see cref="Operations.AutoRemoveWatermark"/>.</summary>
public sealed record AutoRemoveWatermarkParams : IOperationParams
{
    /// <summary>How aggressively to inpaint. Omit for the api default (medium).</summary>
    public WatermarkStrength? Strength { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.AutoRemoveWatermark;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Strength.HasValue)
        {
            into["strength"] = ParamEnum.Wire(Strength.Value);
        }

        return into;
    }
}

/// <summary>Params for <see cref="Operations.ResizeImage"/> — the free operation.</summary>
public sealed record ResizeImageParams : IOperationParams
{
    /// <summary>Target width in pixels, 1..8192. At least one of <see cref="Width"/> / <see cref="Height"/> is required.</summary>
    public int? Width { get; init; }

    /// <summary>Target height in pixels, 1..8192.</summary>
    public int? Height { get; init; }

    /// <summary>How to fit the image into the box. Omit for the api default (inside).</summary>
    public ResizeFit? Fit { get; init; }

    /// <summary>Output encoding. Omit for the api default (png).</summary>
    public ResizeFormat? Format { get; init; }

    /// <summary>Encoder quality, 1..100. Omit for the api default (90). Ignored for png.</summary>
    public int? Quality { get; init; }

    /// <inheritdoc />
    public string Operation => Operations.ResizeImage;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToParams()
    {
        var into = ParamBuilder.New();
        if (Width.HasValue)
        {
            into["width"] = Width.Value;
        }

        if (Height.HasValue)
        {
            into["height"] = Height.Value;
        }

        if (Fit.HasValue)
        {
            into["fit"] = ParamEnum.Wire(Fit.Value);
        }

        if (Format.HasValue)
        {
            into["format"] = ParamEnum.Wire(Format.Value);
        }

        if (Quality.HasValue)
        {
            into["quality"] = Quality.Value;
        }

        return into;
    }
}

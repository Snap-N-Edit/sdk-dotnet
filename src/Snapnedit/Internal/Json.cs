using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Snapnedit.Internal;

/// <summary>
/// Hand-rolled JSON reading and writing.
/// </summary>
/// <remarks>
/// Nothing in this assembly goes through reflection-based
/// <see cref="JsonSerializer"/>: request bodies are written with
/// <see cref="Utf8JsonWriter"/> and responses are narrowed field by field off
/// a <see cref="JsonElement"/>. That keeps the package trim- and AOT-safe
/// without a source-generated context, and — more importantly — lets the
/// client be STRICT where the api is strict and LENIENT where it must be
/// (an unknown enum value degrades instead of throwing).
/// </remarks>
internal static class Json
{
    internal static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions { Indented = false };

    internal static readonly JsonDocumentOptions DocumentOptions = new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    internal static JsonElement? Property(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return obj.TryGetProperty(name, out var value) ? value : (JsonElement?)null;
    }

    internal static string RequireString(JsonElement obj, string name, string source)
    {
        var value = Property(obj, name);
        if (value is null || value.Value.ValueKind != JsonValueKind.String)
        {
            throw Malformed(source, "missing string field \"" + name + "\"");
        }

        return value.Value.GetString()!;
    }

    internal static string? OptionalString(JsonElement obj, string name)
    {
        var value = Property(obj, name);
        return value is not null && value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    internal static bool RequireBool(JsonElement obj, string name, string source)
    {
        var value = Property(obj, name);
        if (value is null || (value.Value.ValueKind != JsonValueKind.True && value.Value.ValueKind != JsonValueKind.False))
        {
            throw Malformed(source, "missing boolean field \"" + name + "\"");
        }

        return value.Value.GetBoolean();
    }

    internal static bool? OptionalBool(JsonElement obj, string name)
    {
        var value = Property(obj, name);
        if (value is null)
        {
            return null;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
    }

    internal static int? OptionalInt(JsonElement obj, string name)
    {
        var value = Property(obj, name);
        if (value is null || value.Value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.Value.TryGetInt32(out var parsed) ? parsed : (int?)null;
    }

    internal static long? OptionalLong(JsonElement obj, string name)
    {
        var value = Property(obj, name);
        if (value is null || value.Value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.Value.TryGetInt64(out var parsed) ? parsed : (long?)null;
    }

    internal static double? OptionalDouble(JsonElement obj, string name)
    {
        var value = Property(obj, name);
        if (value is null || value.Value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.Value.TryGetDouble(out var parsed) ? parsed : (double?)null;
    }

    /// <summary>An object property that is present and is an object; <see langword="null"/> for absent, JSON null or anything else.</summary>
    internal static JsonElement? ObjectProperty(JsonElement obj, string name)
    {
        var value = Property(obj, name);
        return value is not null && value.Value.ValueKind == JsonValueKind.Object ? value : (JsonElement?)null;
    }

    internal static SnapneditException Malformed(string source, string detail) =>
        new SnapneditException(ErrorCode.Internal, source + " returned a malformed response: " + detail, 0);

    /// <summary>
    /// Writes an arbitrary caller-supplied params value. Only the JSON-shaped
    /// .NET types are accepted; anything else throws with a message naming the
    /// offending key, rather than silently reflecting over an object the api
    /// would reject anyway.
    /// </summary>
    internal static void WriteValue(Utf8JsonWriter writer, string keyPath, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case short sh:
                writer.WriteNumberValue(sh);
                return;
            case byte by:
                writer.WriteNumberValue(by);
                return;
            case float f:
                writer.WriteNumberValue(f);
                return;
            case double d:
                writer.WriteNumberValue(d);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var pair in map)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, keyPath + "." + pair.Key, pair.Value);
                }

                writer.WriteEndObject();
                return;
            case IDictionary<string, object?> mutableMap:
                writer.WriteStartObject();
                foreach (var pair in mutableMap)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, keyPath + "." + pair.Key, pair.Value);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in sequence)
                {
                    WriteValue(writer, keyPath + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", item);
                    index++;
                }

                writer.WriteEndArray();
                return;
            default:
                throw new ArgumentException(
                    "params value at \"" + keyPath + "\" has unsupported type " + value.GetType().FullName +
                    " — use a string, number, bool, null, list, dictionary or JsonElement",
                    nameof(value));
        }
    }
}

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MultiLyrics.Configuration;

/// <summary>
/// Tolerant JSON mapping for <see cref="GeniusMode"/>.
/// <para>
/// The host deserializes a posted configuration itself, and its enum handling depends on the
/// Jellyfin line: some builds accept numbers, others only names. A field that a client left
/// blank (empty string or null) used to fail the whole save with
/// "The JSON value could not be converted to GeniusMode" and a 500, which lost every other
/// setting on the page as well. Anything unrecognised now falls back to
/// <see cref="GeniusMode.Off"/> - the safe default - instead of rejecting the request.
/// </para>
/// <para>
/// Values are written as numbers so the settings page always finds a matching option.
/// </para>
/// </summary>
internal sealed class GeniusModeJsonConverter : JsonConverter<GeniusMode>
{
    /// <inheritdoc />
    public override GeniusMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? FromNumber(number) : GeniusMode.Off;

            case JsonTokenType.String:
                return FromString(reader.GetString());

            default:
                // null, an object or an array: not a mode, keep the safe default.
                reader.Skip();
                return GeniusMode.Off;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, GeniusMode value, JsonSerializerOptions options)
        => writer.WriteNumberValue((int)value);

    private static GeniusMode FromNumber(int number)
        => number >= (int)GeniusMode.Off && number <= (int)GeniusMode.Scrape
            ? (GeniusMode)number
            : GeniusMode.Off;

    private static GeniusMode FromString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GeniusMode.Off;
        }

        text = text.Trim();

        // A numeric string ("2") is what an older page wrote.
        if (int.TryParse(text, out var number))
        {
            return FromNumber(number);
        }

        return Enum.TryParse<GeniusMode>(text, ignoreCase: true, out var named) && Enum.IsDefined(named)
            ? named
            : GeniusMode.Off;
    }
}

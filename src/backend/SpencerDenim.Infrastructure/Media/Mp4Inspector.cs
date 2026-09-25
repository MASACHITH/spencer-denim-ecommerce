using System.Buffers.Binary;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>What the MP4 container says about its video track.</summary>
/// <param name="Width">Display width in pixels.</param>
/// <param name="Height">Display height in pixels.</param>
/// <param name="DurationSeconds">From the movie header, to two decimals (the column is <c>decimal(9,2)</c>).</param>
/// <param name="Codec">The sample-entry format: <c>avc1</c> / <c>avc3</c> for H.264.</param>
public sealed record Mp4VideoInfo(int Width, int Height, decimal DurationSeconds, string Codec)
{
    /// <summary>Whether the video track is H.264, which doc 03 §3b makes the only accepted codec.</summary>
    public bool IsH264 => Codec is "avc1" or "avc3";
}

/// <summary>
/// Reads the dimensions, duration and codec out of an MP4's box structure.
/// <para>
/// Doc 03 §3b: "MP4 (H.264) only, max 50 MB, poster image required. <b>No server-side conversion</b>, to
/// protect the 2-vCPU VPS." Nothing here decodes a frame or shells out to ffmpeg — it walks the ISO
/// base-media boxes, which is a few kilobytes of reading whatever the file's size.
/// </para>
/// <para>
/// <b>The file is hostile until proved otherwise.</b> Every box length is checked against its parent's
/// end before it is followed, a 64-bit length that would overflow is refused, a zero-length box cannot
/// make the walk stand still, the nesting depth is capped and the total number of boxes is capped — so a
/// file crafted with a million eight-byte boxes, or a box that claims to contain itself, is rejected
/// rather than allowed to spin. Nothing is allocated in proportion to a declared length.
/// </para>
/// </summary>
public static class Mp4Inspector
{
    /// <summary>How many boxes the walk will look at in total before giving up.</summary>
    private const int BoxBudget = 4096;

    /// <summary>How many boxes one container may hold before the walk gives up.</summary>
    private const int MaxChildrenPerBox = 512;

    /// <summary>
    /// Inspects a seekable MP4 stream.
    /// </summary>
    /// <returns>
    /// The video track's details, or <see langword="null"/> when the file has no <c>moov</c>, no video
    /// track, or a structure that does not parse. A caller that gets null refuses the upload; it never
    /// stores a file it could not read.
    /// </returns>
    public static Mp4VideoInfo? TryInspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The MP4 inspector needs a seekable stream.", nameof(stream));
        }

        try
        {
            var budget = new Budget();
            var length = stream.Length;

            var top = ReadBoxes(stream, 0, length, budget);
            if (Find(top, "moov") is not { } moov)
            {
                return null;
            }

            var movie = ReadBoxes(stream, moov.PayloadStart, moov.End, budget);

            var (timescale, duration) = ReadMovieHeader(stream, Find(movie, "mvhd"));

            foreach (var track in movie)
            {
                if (!string.Equals(track.Type, "trak", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReadTrack(stream, track, budget) is { } video)
                {
                    var seconds = timescale > 0
                        ? Math.Round((decimal)duration / timescale, 2, MidpointRounding.AwayFromZero)
                        : 0m;

                    // decimal(9,2) holds up to 9 999 999.99 seconds; a value past that is a broken header.
                    if (seconds is < 0m or > 9_999_999.99m)
                    {
                        seconds = 0m;
                    }

                    return video with { DurationSeconds = seconds };
                }
            }

            return null;
        }
        catch (EndOfStreamException)
        {
            // A truncated file: the header said there was more than there is.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static Mp4VideoInfo? ReadTrack(Stream stream, Box track, Budget budget)
    {
        var children = ReadBoxes(stream, track.PayloadStart, track.End, budget);

        if (Find(children, "mdia") is not { } media)
        {
            return null;
        }

        var mediaChildren = ReadBoxes(stream, media.PayloadStart, media.End, budget);

        if (Find(mediaChildren, "hdlr") is not { } handler || !IsVideoHandler(stream, handler))
        {
            return null;
        }

        if (Find(mediaChildren, "minf") is not { } information)
        {
            return null;
        }

        var informationChildren = ReadBoxes(stream, information.PayloadStart, information.End, budget);
        if (Find(informationChildren, "stbl") is not { } table)
        {
            return null;
        }

        var tableChildren = ReadBoxes(stream, table.PayloadStart, table.End, budget);
        if (Find(tableChildren, "stsd") is not { } description)
        {
            return null;
        }

        var sample = ReadFirstSampleEntry(stream, description);
        if (sample is null)
        {
            return null;
        }

        var (codec, sampleWidth, sampleHeight) = sample.Value;

        // The track header carries the display size, which is what a browser lays out; the sample entry
        // carries the coded size. They differ for anamorphic video, so prefer the track header and fall
        // back when it is absent or zero (which some muxers write).
        var (trackWidth, trackHeight) = Find(children, "tkhd") is { } trackHeader
            ? ReadTrackHeaderSize(stream, trackHeader)
            : (0, 0);

        var width = trackWidth > 0 ? trackWidth : sampleWidth;
        var height = trackHeight > 0 ? trackHeight : sampleHeight;

        return width > 0 && height > 0
            ? new Mp4VideoInfo(width, height, 0m, codec)
            : null;
    }

    private static bool IsVideoHandler(Stream stream, Box handler)
    {
        // hdlr: version+flags (4), pre_defined (4), handler_type (4).
        if (handler.PayloadLength < 12)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[4];
        Read(stream, handler.PayloadStart + 8, buffer);
        return Ascii(buffer) is "vide";
    }

    private static (uint Timescale, ulong Duration) ReadMovieHeader(Stream stream, Box? header)
    {
        if (header is not { } mvhd || mvhd.PayloadLength < 4)
        {
            return (0, 0);
        }

        Span<byte> version = stackalloc byte[1];
        Read(stream, mvhd.PayloadStart, version);

        if (version[0] == 1)
        {
            // version 1: creation (8), modification (8), timescale (4), duration (8).
            if (mvhd.PayloadLength < 32)
            {
                return (0, 0);
            }

            Span<byte> buffer = stackalloc byte[12];
            Read(stream, mvhd.PayloadStart + 20, buffer);
            return (BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]),
                BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(4, 8)));
        }

        // version 0: creation (4), modification (4), timescale (4), duration (4).
        if (mvhd.PayloadLength < 20)
        {
            return (0, 0);
        }

        Span<byte> shortBuffer = stackalloc byte[8];
        Read(stream, mvhd.PayloadStart + 12, shortBuffer);
        return (BinaryPrimitives.ReadUInt32BigEndian(shortBuffer[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(shortBuffer.Slice(4, 4)));
    }

    private static (int Width, int Height) ReadTrackHeaderSize(Stream stream, Box header)
    {
        if (header.PayloadLength < 4)
        {
            return (0, 0);
        }

        Span<byte> version = stackalloc byte[1];
        Read(stream, header.PayloadStart, version);

        // Width and height are the last two fields, 16.16 fixed point, after a 36-byte matrix.
        var offset = version[0] == 1 ? 88 : 76;
        if (header.PayloadLength < offset + 8)
        {
            return (0, 0);
        }

        Span<byte> buffer = stackalloc byte[8];
        Read(stream, header.PayloadStart + offset, buffer);

        var width = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]) >> 16;
        var height = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)) >> 16;

        return ((int)width, (int)height);
    }

    private static (string Codec, int Width, int Height)? ReadFirstSampleEntry(Stream stream, Box description)
    {
        // stsd: version+flags (4), entry_count (4), then the entries.
        // A visual sample entry is: size (4), format (4), reserved (6), data_reference_index (2),
        // pre_defined+reserved (16), width (2), height (2).
        if (description.PayloadLength < 8 + 32)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[32];
        Read(stream, description.PayloadStart + 8, buffer);

        var entrySize = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
        if (entrySize < 16 || entrySize > description.PayloadLength - 8)
        {
            return null;
        }

        var codec = Ascii(buffer.Slice(4, 4));
        var width = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(24, 2));
        var height = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(26, 2));

        return (codec, width, height);
    }

    // ------------------------------------------------------------ box walking

    private readonly record struct Box(string Type, long PayloadStart, long End)
    {
        public long PayloadLength => End - PayloadStart;
    }

    private sealed class Budget
    {
        public int Remaining { get; set; } = BoxBudget;
    }

    private static Box? Find(List<Box> boxes, string type)
    {
        foreach (var box in boxes)
        {
            if (string.Equals(box.Type, type, StringComparison.Ordinal))
            {
                return box;
            }
        }

        return null;
    }

    private static List<Box> ReadBoxes(Stream stream, long start, long end, Budget budget)
    {
        var boxes = new List<Box>();
        var position = start;

        // Outside the loop deliberately (CA2014): one 16-byte frame, reused, rather than one per box.
        Span<byte> header = stackalloc byte[16];

        while (position + 8 <= end && boxes.Count < MaxChildrenPerBox)
        {
            if (budget.Remaining-- <= 0)
            {
                return boxes;
            }

            Read(stream, position, header[..8]);

            long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = Ascii(header.Slice(4, 4));
            var payloadStart = position + 8;

            if (size == 1)
            {
                // A 64-bit "largesize" follows the type.
                if (position + 16 > end)
                {
                    return boxes;
                }

                Read(stream, position + 8, header.Slice(8, 8));
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                if (large > long.MaxValue)
                {
                    return boxes;
                }

                size = (long)large;
                payloadStart = position + 16;
            }
            else if (size == 0)
            {
                // "To the end of the container" — legal, and only for the last box.
                size = end - position;
            }

            var headerLength = payloadStart - position;
            if (size < headerLength)
            {
                // A box smaller than its own header, or a zero-length one: following it would either move
                // backwards or not move at all.
                return boxes;
            }

            var boxEnd = position + size;
            if (boxEnd > end || boxEnd <= position)
            {
                // Declared past its parent, or overflowed.
                return boxes;
            }

            boxes.Add(new Box(type, payloadStart, boxEnd));
            position = boxEnd;
        }

        return boxes;
    }

    private static void Read(Stream stream, long position, Span<byte> buffer)
    {
        stream.Position = position;
        stream.ReadExactly(buffer);
    }

    private static string Ascii(ReadOnlySpan<byte> four)
    {
        Span<char> characters = stackalloc char[four.Length];
        for (var index = 0; index < four.Length; index++)
        {
            var value = four[index];

            // A box type is four printable ASCII characters. Anything else is not a type we will match,
            // and turning it into a control character in a log line helps nobody.
            characters[index] = value is >= 0x20 and <= 0x7E ? (char)value : '?';
        }

        return new string(characters).Trim();
    }
}

using System.Buffers.Binary;
using System.Text;

namespace SpencerDenim.Tests.MediaFixtures;

/// <summary>
/// Builds MP4 containers box by box, so the inspector can be tested against a file whose every field is
/// known — including the malformed ones no real encoder would produce and a hostile uploader would.
/// <para>
/// There is no sample video checked in and none is needed: <c>Mp4Inspector</c> reads the container, never a
/// frame, so a file with a correct <c>moov</c> and no media data exercises exactly the code under test.
/// The end-to-end verification against a real encoded MP4 is a separate, manual step.
/// </para>
/// </summary>
public static class TestVideos
{
    /// <summary>A well-formed MP4 with one H.264 video track.</summary>
    /// <param name="width">Display width written into the track header.</param>
    /// <param name="height">Display height written into the track header.</param>
    /// <param name="durationSeconds">Duration, at a timescale of 1000.</param>
    /// <param name="codec">The sample-entry format: <c>avc1</c> for H.264, <c>hvc1</c> for HEVC.</param>
    /// <param name="handler">The media handler: <c>vide</c> for a video track, <c>soun</c> for audio.</param>
    /// <param name="brand">The <c>ftyp</c> major brand.</param>
    /// <param name="payloadBytes">How many bytes of dummy <c>mdat</c> to append.</param>
    public static byte[] Mp4(
        int width = 1280,
        int height = 720,
        double durationSeconds = 12.5,
        string codec = "avc1",
        string handler = "vide",
        string brand = "isom",
        int payloadBytes = 1024,
        byte[]? payload = null)
    {
        const uint timescale = 1000;
        var duration = (uint)Math.Round(durationSeconds * timescale);

        var ftyp = Box("ftyp", Concat(Ascii(brand), BigEndian(0x200u), Ascii("isom"), Ascii("mp41")));
        var mvhd = Box("mvhd", MovieHeader(timescale, duration));
        var trak = Box("trak", Concat(
            Box("tkhd", TrackHeader(width, height, duration)),
            Box("mdia", Concat(
                Box("hdlr", Handler(handler)),
                Box("minf", Box("stbl", Box("stsd", SampleDescription(codec, width, height))))))));

        var moov = Box("moov", Concat(mvhd, trak));
        var mdat = Box("mdat", payload ?? new byte[payloadBytes]);

        return Concat(ftyp, moov, mdat);
    }

    /// <summary>
    /// An MP4 whose media data no other call will produce, so the checksum rule does not answer one test's
    /// upload with another test's asset. See <c>TestImages.UniqueJpeg</c> for the same reasoning.
    /// </summary>
    public static byte[] UniqueMp4(
        int width = 1280,
        int height = 720,
        double durationSeconds = 12.5,
        int payloadBytes = 1024)
    {
        var payload = new byte[payloadBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(payload);

        return Mp4(width, height, durationSeconds, payloadBytes: payloadBytes, payload: payload);
    }

    /// <summary>An MP4 whose <c>moov</c> box claims to be far longer than the file.</summary>
    public static byte[] Mp4WithOversizedBox()
    {
        var file = Mp4();
        var moovStart = IndexOf(file, Ascii("moov")) - 4;
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(moovStart, 4), 0x7FFFFFFF);
        return file;
    }

    /// <summary>
    /// An MP4 whose first box after <c>ftyp</c> declares a length of zero — the shape that makes a naive
    /// walker loop for ever.
    /// </summary>
    public static byte[] Mp4WithZeroLengthBox()
    {
        var ftyp = Box("ftyp", Concat(Ascii("isom"), BigEndian(0x200u)));
        var zero = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(zero.AsSpan(0, 4), 0);
        Ascii("junk").CopyTo(zero.AsSpan(4, 4));
        return Concat(ftyp, zero, Box("moov", []));
    }

    /// <summary>Thousands of tiny boxes: the budget guard's target.</summary>
    public static byte[] Mp4WithManyTinyBoxes(int count)
    {
        using var stream = new MemoryStream();
        stream.Write(Box("ftyp", Concat(Ascii("isom"), BigEndian(0x200u))));

        for (var index = 0; index < count; index++)
        {
            stream.Write(Box("free", []));
        }

        return stream.ToArray();
    }

    /// <summary>
    /// An MP4 cut off part way through its <c>moov</c> box, as an interrupted upload would be. The cut has
    /// to land inside <c>moov</c> to mean anything: <c>moov</c> comes before <c>mdat</c> in a
    /// web-optimised file, so losing only the media data still leaves a readable header.
    /// </summary>
    public static byte[] TruncatedMp4()
    {
        var file = Mp4();
        return file.AsSpan(0, 200).ToArray();
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] MovieHeader(uint timescale, uint duration)
    {
        var payload = new byte[100];
        // version 0 + flags, creation, modification, timescale, duration, then the rest can stay zero.
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), duration);
        return payload;
    }

    private static byte[] TrackHeader(int width, int height, uint duration)
    {
        var payload = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), 1); // track id
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), duration);

        // 16.16 fixed point, at offsets 76 and 80 of a version 0 track header.
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(76, 4), (uint)width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(80, 4), (uint)height << 16);
        return payload;
    }

    private static byte[] Handler(string handler)
    {
        var payload = new byte[32];
        Ascii(handler).CopyTo(payload.AsSpan(8, 4));
        Encoding.ASCII.GetBytes("Test\0").CopyTo(payload.AsSpan(24));
        return payload;
    }

    private static byte[] SampleDescription(string codec, int width, int height)
    {
        // version + flags (4), entry count (4), then one visual sample entry of 86 bytes.
        var entry = new byte[86];
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(0, 4), (uint)entry.Length);
        Ascii(codec).CopyTo(entry.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(14, 2), 1); // data reference index
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26, 2), (ushort)height);

        var payload = new byte[8 + entry.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 1);
        entry.CopyTo(payload.AsSpan(8));
        return payload;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Ascii(type).CopyTo(box.AsSpan(4, 4));
        payload.CopyTo(box.AsSpan(8));
        return box;
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(part => part.Length);
        var result = new byte[total];
        var offset = 0;

        foreach (var part in parts)
        {
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return index;
            }
        }

        return -1;
    }
}

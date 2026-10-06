using System.Buffers.Binary;
using System.Text;

namespace FolderMorpher.Services;

// GDI+ does not expose all ICC/XMP/Exif blocks. Preserve container bytes instead.
internal static class ImageMetadataPreserver
{
    private sealed record Part(string Kind, byte[] Bytes);

    internal static byte[] Transfer(byte[] original, byte[] encoded, bool png)
    {
        var oldParts = png ? PngParts(original) : JpegParts(original);
        var newParts = png ? PngParts(encoded) : JpegParts(encoded);
        bool Metadata(Part part) => png ? IsPngMetadata(part.Kind) : part.Kind == "metadata";
        var originalMetadata = oldParts.Where(Metadata).ToList();
        using var result = new MemoryStream();
        if (png)
        {
            result.Write(encoded, 0, 8);
            foreach (var part in newParts.Where(p => !Metadata(p)))
            {
                result.Write(part.Bytes);
                if (part.Kind == "IHDR") foreach (var metadata in originalMetadata) result.Write(metadata.Bytes);
            }
        }
        else
        {
            result.WriteByte(0xff); result.WriteByte(0xd8);
            foreach (var metadata in originalMetadata) result.Write(metadata.Bytes);
            foreach (var part in newParts.Where(p => !Metadata(p))) result.Write(part.Bytes);
        }
        byte[] output = result.ToArray();
        var actual = (png ? PngParts(output) : JpegParts(output)).Where(Metadata).ToList();
        if (actual.Count != originalMetadata.Count || actual.Where((p, i) => !p.Bytes.SequenceEqual(originalMetadata[i].Bytes)).Any())
            throw new IOException("Image metadata preservation failed; original retained.");
        return output;
    }

    private static bool IsPngMetadata(string kind)
    {
        if (kind is "IHDR" or "IDAT" or "IEND" or "PLTE" or "tRNS") return false;
        if (kind is "acTL" or "fcTL" or "fdAT") throw new IOException("Animated PNG requires a separate workflow; original retained.");
        if (kind is "bKGD" or "hIST" or "sBIT") throw new IOException("Color-dependent PNG chunks cannot safely survive this conversion; original retained.");
        if (kind is "cHRM" or "gAMA" or "iCCP" or "sRGB" or "pHYs" or "tIME" or "tEXt" or "zTXt" or "iTXt" or "eXIf") return true;
        if (char.IsLower(kind[0]) && char.IsLower(kind[3])) return true; // PNG safe-to-copy flag.
        throw new IOException("Unknown unsafe PNG chunk; original retained.");
    }

    private static List<Part> PngParts(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new IOException("Invalid PNG signature.");
        var parts = new List<Part>();
        int offset = 8;
        while (offset < data.Length)
        {
            if (data.Length - offset < 12) throw new IOException("Truncated PNG chunk.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (length > int.MaxValue - 12 || length + 12 > data.Length - offset) throw new IOException("Invalid PNG chunk size.");
            int count = (int)length + 12;
            string kind = Encoding.ASCII.GetString(data, offset + 4, 4);
            parts.Add(new(kind, data.AsSpan(offset, count).ToArray()));
            offset += count;
        }
        if (parts.Count == 0 || parts[0].Kind != "IHDR" || parts[^1].Kind != "IEND") throw new IOException("Incomplete PNG container.");
        return parts;
    }

    private static List<Part> JpegParts(byte[] data)
    {
        if (data.Length < 4 || data[0] != 255 || data[1] != 216) throw new IOException("Invalid JPEG signature.");
        var parts = new List<Part>();
        int offset = 2;
        while (offset < data.Length)
        {
            int start = offset;
            if (data[offset++] != 255) throw new IOException("Invalid JPEG marker.");
            while (offset < data.Length && data[offset] == 255) offset++;
            if (offset >= data.Length) throw new IOException("Truncated JPEG marker.");
            byte marker = data[offset++];
            if (marker == 218) { parts.Add(new("pixels", data.AsSpan(start).ToArray())); return parts; }
            if (marker == 217) { parts.Add(new("end", data.AsSpan(start, offset - start).ToArray())); return parts; }
            if (data.Length - offset < 2) throw new IOException("Truncated JPEG segment.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            if (length < 2 || length > data.Length - offset) throw new IOException("Invalid JPEG segment length.");
            offset += length;
            parts.Add(new(marker is >= 224 and <= 239 or 254 ? "metadata" : "image", data.AsSpan(start, offset - start).ToArray()));
        }
        throw new IOException("Incomplete JPEG container.");
    }
}

using System.Text;

namespace FrameStack.Emulation.Images;

public sealed class BinaryImageAnalyzer : IImageAnalyzer
{
    private const byte ElfMagic0 = 0x7F;
    private const byte ElfMagic1 = (byte)'E';
    private const byte ElfMagic2 = (byte)'L';
    private const byte ElfMagic3 = (byte)'F';
    private static readonly byte[] CiscoFamilyMarker = "CW_FAMILY$"u8.ToArray();
    private static readonly byte[] CiscoImageMarker = "CW_IMAGE$"u8.ToArray();

    public ImageInspectionResult Analyze(ReadOnlySpan<byte> imageBytes)
    {
        if (imageBytes.Length < 4)
        {
            throw new InvalidOperationException("Image is too small.");
        }

        if (imageBytes[0] == ElfMagic0 && imageBytes[1] == ElfMagic1 && imageBytes[2] == ElfMagic2 && imageBytes[3] == ElfMagic3)
        {
            return AnalyzeElf32(imageBytes);
        }

        if (imageBytes[0] == 0x1F && imageBytes[1] == 0x8B)
        {
            return new ImageInspectionResult(
                ImageContainerFormat.Gzip,
                ImageArchitecture.Unknown,
                ImageEndianness.Unknown,
                0,
                [],
                "GZIP-compressed image; decompression is required before execution.");
        }

        if (imageBytes[0] == (byte)'P' && imageBytes[1] == (byte)'K')
        {
            return new ImageInspectionResult(
                ImageContainerFormat.Zip,
                ImageArchitecture.Unknown,
                ImageEndianness.Unknown,
                0,
                [],
                "ZIP container image; extraction is required before execution.");
        }

        return new ImageInspectionResult(
            ImageContainerFormat.RawBinary,
            ImageArchitecture.Mips32,
            ImageEndianness.BigEndian,
            0,
            [],
            "Raw binary image. Defaulting to MIPS32 big-endian bootstrap.");
    }

    private static ImageInspectionResult AnalyzeElf32(ReadOnlySpan<byte> imageBytes)
    {
        if (imageBytes.Length < 52)
        {
            throw new InvalidOperationException("ELF image is too small for ELF32 header.");
        }

        var elfClass = imageBytes[4];
        var dataEncoding = imageBytes[5];

        if (elfClass != 1)
        {
            throw new NotSupportedException($"Only ELF32 is supported. Encountered class value '{elfClass}'.");
        }

        var endianness = dataEncoding switch
        {
            1 => ImageEndianness.LittleEndian,
            2 => ImageEndianness.BigEndian,
            _ => throw new NotSupportedException($"Unsupported ELF data encoding '{dataEncoding}'.")
        };

        var machine = ReadUInt16(imageBytes, 18, endianness);
        var architecture = machine switch
        {
            8 => ImageArchitecture.Mips32,
            20 => ImageArchitecture.PowerPc32,
            43 => ImageArchitecture.SparcV9,
            _ => ImageArchitecture.Unknown
        };

        var entryPoint = ReadUInt32(imageBytes, 24, endianness);
        var programHeaderOffset = ReadUInt32(imageBytes, 28, endianness);
        var programHeaderSize = ReadUInt16(imageBytes, 42, endianness);
        var programHeaderCount = ReadUInt16(imageBytes, 44, endianness);

        var sections = new List<ImageSectionDescriptor>(programHeaderCount);

        for (var index = 0; index < programHeaderCount; index++)
        {
            var headerOffset = checked((int)(programHeaderOffset + (uint)(index * programHeaderSize)));

            if (headerOffset < 0 || headerOffset + 32 > imageBytes.Length)
            {
                throw new InvalidOperationException("Program header points outside the image range.");
            }

            var programType = ReadUInt32(imageBytes, headerOffset, endianness);

            if (programType != 1)
            {
                continue;
            }

            var fileOffset = ReadUInt32(imageBytes, headerOffset + 4, endianness);
            var virtualAddress = ReadUInt32(imageBytes, headerOffset + 8, endianness);
            var fileSize = ReadUInt32(imageBytes, headerOffset + 16, endianness);
            var memorySize = ReadUInt32(imageBytes, headerOffset + 20, endianness);
            var flags = ReadUInt32(imageBytes, headerOffset + 24, endianness);

            sections.Add(new ImageSectionDescriptor(
                virtualAddress,
                fileOffset,
                fileSize,
                memorySize,
                Readable: (flags & 0x4) != 0,
                Writable: (flags & 0x2) != 0,
                Executable: (flags & 0x1) != 0));
        }

        var summary = $"ELF32 image with {sections.Count} loadable segment(s).";

        if (machine == 43 && endianness == ImageEndianness.BigEndian &&
            TryLooksLikePowerPcEntry(imageBytes, sections, entryPoint))
        {
            architecture = ImageArchitecture.PowerPc32;
            summary = $"{summary} ELF machine reports SparcV9, entry signature matches PowerPC32.";
        }

        var ciscoFamily = TryExtractCiscoTagValue(imageBytes, CiscoFamilyMarker);
        var ciscoImageTag = TryExtractCiscoTagValue(imageBytes, CiscoImageMarker);

        if (ciscoFamily is not null)
        {
            summary = $"{summary} Cisco family tag: {ciscoFamily}.";
        }

        if (ciscoImageTag is not null)
        {
            summary = $"{summary} Cisco image tag: {ciscoImageTag}.";
        }

        return new ImageInspectionResult(
            ImageContainerFormat.Elf32,
            architecture,
            endianness,
            entryPoint,
            sections,
            summary,
            ciscoFamily,
            ciscoImageTag);
    }

    private static bool TryLooksLikePowerPcEntry(
        ReadOnlySpan<byte> imageBytes,
        IReadOnlyList<ImageSectionDescriptor> sections,
        uint entryPoint)
    {
        foreach (var section in sections)
        {
            var sectionStart = section.VirtualAddress;
            var sectionEnd = unchecked(section.VirtualAddress + section.FileSize);

            if (entryPoint < sectionStart || entryPoint + 8 > sectionEnd)
            {
                continue;
            }

            var entryOffset = checked((int)(section.FileOffset + (entryPoint - section.VirtualAddress)));

            if (entryOffset < 0 || entryOffset + 8 > imageBytes.Length)
            {
                return false;
            }

            var firstInstruction =
                ((uint)imageBytes[entryOffset] << 24) |
                ((uint)imageBytes[entryOffset + 1] << 16) |
                ((uint)imageBytes[entryOffset + 2] << 8) |
                imageBytes[entryOffset + 3];

            var secondInstruction =
                ((uint)imageBytes[entryOffset + 4] << 24) |
                ((uint)imageBytes[entryOffset + 5] << 16) |
                ((uint)imageBytes[entryOffset + 6] << 8) |
                imageBytes[entryOffset + 7];

            // Common PPC function prologue in IOS images: stwu r1, -X(r1); mflr r0
            if ((firstInstruction & 0xFFFF_0000) == 0x9421_0000 && secondInstruction == 0x7C08_02A6)
            {
                return true;
            }

            return false;
        }

        return false;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset, ImageEndianness endianness)
    {
        return endianness switch
        {
            ImageEndianness.LittleEndian => (ushort)(bytes[offset] | (bytes[offset + 1] << 8)),
            ImageEndianness.BigEndian => (ushort)((bytes[offset] << 8) | bytes[offset + 1]),
            _ => throw new InvalidOperationException("Unknown endianness.")
        };
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset, ImageEndianness endianness)
    {
        return endianness switch
        {
            ImageEndianness.LittleEndian =>
                (uint)(bytes[offset] |
                       (bytes[offset + 1] << 8) |
                       (bytes[offset + 2] << 16) |
                       (bytes[offset + 3] << 24)),
            ImageEndianness.BigEndian =>
                (uint)((bytes[offset] << 24) |
                       (bytes[offset + 1] << 16) |
                       (bytes[offset + 2] << 8) |
                       bytes[offset + 3]),
            _ => throw new InvalidOperationException("Unknown endianness.")
        };
    }

    private static string? TryExtractCiscoTagValue(ReadOnlySpan<byte> imageBytes, ReadOnlySpan<byte> marker)
    {
        var markerIndex = imageBytes.IndexOf(marker);

        if (markerIndex < 0)
        {
            return null;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = valueStart;

        while (valueEnd < imageBytes.Length)
        {
            var currentByte = imageBytes[valueEnd];

            if (currentByte == (byte)'$' || currentByte == 0)
            {
                break;
            }

            if (currentByte < 0x20 || currentByte > 0x7E)
            {
                return null;
            }

            valueEnd++;
        }

        if (valueEnd <= valueStart)
        {
            return null;
        }

        return Encoding.ASCII.GetString(imageBytes.Slice(valueStart, valueEnd - valueStart));
    }
}

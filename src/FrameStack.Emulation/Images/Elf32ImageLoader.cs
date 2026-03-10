namespace FrameStack.Emulation.Images;

public sealed class Elf32ImageLoader : IImageLoader
{
    public bool CanLoad(ImageInspectionResult inspection)
    {
        return inspection.Format == ImageContainerFormat.Elf32;
    }

    public LoadedImage Load(
        ReadOnlySpan<byte> imageBytes,
        ImageInspectionResult inspection,
        ImageLoadOptions options)
    {
        if (!CanLoad(inspection))
        {
            throw new InvalidOperationException("ELF32 loader cannot handle this image format.");
        }

        var segments = new List<LoadedImageSegment>(inspection.Sections.Count);

        foreach (var section in inspection.Sections)
        {
            if (section.FileSize == 0)
            {
                segments.Add(new LoadedImageSegment(
                    section.VirtualAddress,
                    [],
                    section.MemorySize,
                    section.Executable));

                continue;
            }

            var offset = checked((int)section.FileOffset);
            var length = checked((int)section.FileSize);

            if (offset < 0 || offset + length > imageBytes.Length)
            {
                throw new InvalidOperationException("ELF section points outside image bounds.");
            }

            var sectionBytes = imageBytes.Slice(offset, length).ToArray();

            segments.Add(new LoadedImageSegment(
                section.VirtualAddress,
                sectionBytes,
                section.MemorySize,
                section.Executable));
        }

        return new LoadedImage(
            inspection.Format,
            inspection.Architecture,
            inspection.Endianness,
            inspection.EntryPoint,
            segments);
    }
}

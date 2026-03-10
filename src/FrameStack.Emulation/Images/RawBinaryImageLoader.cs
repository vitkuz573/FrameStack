namespace FrameStack.Emulation.Images;

public sealed class RawBinaryImageLoader : IImageLoader
{
    public bool CanLoad(ImageInspectionResult inspection)
    {
        return inspection.Format == ImageContainerFormat.RawBinary;
    }

    public LoadedImage Load(
        ReadOnlySpan<byte> imageBytes,
        ImageInspectionResult inspection,
        ImageLoadOptions options)
    {
        if (!CanLoad(inspection))
        {
            throw new InvalidOperationException("Raw loader cannot handle this image format.");
        }

        var entryPoint = options.RawImageEntryPoint ?? options.RawImageBaseAddress;

        var segments = new[]
        {
            new LoadedImageSegment(
                options.RawImageBaseAddress,
                imageBytes.ToArray(),
                (uint)imageBytes.Length,
                Executable: true)
        };

        return new LoadedImage(
            inspection.Format,
            inspection.Architecture,
            inspection.Endianness,
            entryPoint,
            segments);
    }
}

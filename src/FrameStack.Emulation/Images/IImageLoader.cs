namespace FrameStack.Emulation.Images;

public interface IImageLoader
{
    bool CanLoad(ImageInspectionResult inspection);

    LoadedImage Load(
        ReadOnlySpan<byte> imageBytes,
        ImageInspectionResult inspection,
        ImageLoadOptions options);
}

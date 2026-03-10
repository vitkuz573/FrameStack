namespace FrameStack.Emulation.Images;

public interface IImageAnalyzer
{
    ImageInspectionResult Analyze(ReadOnlySpan<byte> imageBytes);
}

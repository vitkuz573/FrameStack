namespace FrameStack.Emulation.Images;

public sealed record ImageLoadOptions(
    uint RawImageBaseAddress,
    uint? RawImageEntryPoint)
{
    public static ImageLoadOptions Default { get; } = new(
        RawImageBaseAddress: 0x1000,
        RawImageEntryPoint: null);
}

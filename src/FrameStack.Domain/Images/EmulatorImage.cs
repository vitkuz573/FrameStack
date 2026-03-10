using FrameStack.Domain.Abstractions;

namespace FrameStack.Domain.Images;

public sealed class EmulatorImage : AggregateRoot<ImageId>
{
    private EmulatorImage(
        ImageId id,
        string vendor,
        ImagePlatform platform,
        string name,
        string version,
        string artifactPath,
        string? sha256,
        long? declaredSizeBytes,
        DateTimeOffset registeredAtUtc)
        : base(id)
    {
        Vendor = vendor;
        Platform = platform;
        Name = name;
        Version = version;
        ArtifactPath = artifactPath;
        Sha256 = sha256;
        DeclaredSizeBytes = declaredSizeBytes;
        RegisteredAtUtc = registeredAtUtc;
    }

    public string Vendor { get; }

    public ImagePlatform Platform { get; }

    public string Name { get; }

    public string Version { get; }

    public string ArtifactPath { get; }

    public string? Sha256 { get; }

    public long? DeclaredSizeBytes { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public static EmulatorImage Register(
        string vendor,
        ImagePlatform platform,
        string name,
        string version,
        string artifactPath,
        string? sha256,
        long? declaredSizeBytes,
        DateTimeOffset registeredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(vendor))
        {
            throw new ArgumentException("Vendor is required.", nameof(vendor));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Version is required.", nameof(version));
        }

        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            throw new ArgumentException("Artifact path is required.", nameof(artifactPath));
        }

        return new EmulatorImage(
            ImageId.New(),
            vendor.Trim(),
            platform,
            name.Trim(),
            version.Trim(),
            artifactPath.Trim(),
            string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant(),
            declaredSizeBytes,
            registeredAtUtc);
    }
}

using FrameStack.Domain.Abstractions;
using FrameStack.Domain.Images;
using FrameStack.Domain.Sessions.Events;

namespace FrameStack.Domain.Sessions;

public sealed class EmulationSession : AggregateRoot<SessionId>
{
    private EmulationSession(
        SessionId id,
        ImageId imageId,
        SessionResources resources,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        ImageId = imageId;
        Resources = resources;
        CreatedAtUtc = createdAtUtc;
        LastTransitionAtUtc = createdAtUtc;
        Status = EmulationSessionStatus.Created;
    }

    public ImageId ImageId { get; }

    public SessionResources Resources { get; }

    public EmulationSessionStatus Status { get; private set; }

    public string? RuntimeHandle { get; private set; }

    public string? RuntimeArtifactPath { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    public static EmulationSession Create(ImageId imageId, SessionResources resources, DateTimeOffset createdAtUtc)
    {
        return new EmulationSession(SessionId.New(), imageId, resources, createdAtUtc);
    }

    public void MarkPreparingImage(DateTimeOffset nowUtc)
    {
        EnsureStatus(EmulationSessionStatus.Created, EmulationSessionStatus.Ready);
        TransitionTo(EmulationSessionStatus.PreparingImage, nowUtc);
    }

    public void MarkReady(string artifactPath, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            throw new ArgumentException("Artifact path is required.", nameof(artifactPath));
        }

        EnsureStatus(EmulationSessionStatus.PreparingImage, EmulationSessionStatus.Created);
        RuntimeArtifactPath = artifactPath;
        TransitionTo(EmulationSessionStatus.Ready, nowUtc);
    }

    public void Start(string runtimeHandle, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(runtimeHandle))
        {
            throw new ArgumentException("Runtime handle is required.", nameof(runtimeHandle));
        }

        EnsureStatus(EmulationSessionStatus.Ready, EmulationSessionStatus.Stopped);

        RuntimeHandle = runtimeHandle;
        LastError = null;
        TransitionTo(EmulationSessionStatus.Running, nowUtc);

        RaiseDomainEvent(new EmulationSessionStartedDomainEvent(Id, runtimeHandle, nowUtc));
    }

    public void Stop(DateTimeOffset nowUtc)
    {
        EnsureStatus(EmulationSessionStatus.Running);
        TransitionTo(EmulationSessionStatus.Stopped, nowUtc);
    }

    public void Fail(string reason, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason is required.", nameof(reason));
        }

        LastError = reason.Trim();
        TransitionTo(EmulationSessionStatus.Failed, nowUtc);
    }

    private void EnsureStatus(params EmulationSessionStatus[] supported)
    {
        if (supported.Contains(Status))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Session status transition is not allowed from '{Status}'. Supported: {string.Join(", ", supported)}");
    }

    private void TransitionTo(EmulationSessionStatus newStatus, DateTimeOffset nowUtc)
    {
        Status = newStatus;
        LastTransitionAtUtc = nowUtc;
    }
}

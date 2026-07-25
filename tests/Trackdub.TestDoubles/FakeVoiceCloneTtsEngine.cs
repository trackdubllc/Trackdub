using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeVoiceCloneTtsEngine(IConsentService consentService) : FakeTtsEngine
{
    private readonly IConsentService consentService = consentService ?? throw new ArgumentNullException(nameof(consentService));

    public Guid? LastReferenceClipArtifactId { get; private set; }

    public override Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        if (request.VoiceCloneReference is not null &&
            !consentService.IsVoiceCloningConsentGranted)
        {
            throw new ConsentRequiredException();
        }

        LastReferenceClipArtifactId = request.VoiceCloneReference?.ReferenceClipArtifactId;
        return base.SynthesizeAsync(request, cancellationToken);
    }
}

using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;

namespace Trackdub.Composition.DeepFilterNet;

internal sealed class DeepFilterNetSpeechAudioEnhancementService(
    DeepFilterNetEnhancementEngine deepFilterNet,
    ISpeechAudioEnhancementService fallback) : ISpeechAudioEnhancementService
{
    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await deepFilterNet.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (RequiredModelNotAvailableException)
        {
            return await fallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

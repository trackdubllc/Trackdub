namespace Trackdub.Inference.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Text-to-speech inference service.
/// </summary>
public interface ITtsService
{
    /// <summary>Generate speech from text using specified voice.</summary>
    Task<byte[]> GenerateSpeechAsync(
        string text,
        Voice voice,
        string targetLanguage,
        double? targetDuration = null,
        CancellationToken cancellationToken = default);
}

namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Translates transcript segments to target language.
/// </summary>
public interface ITranslationService
{
    /// <summary>Translate transcript segments while preserving timing and context.</summary>
    Task<List<TranslatedSegment>> TranslateSegmentsAsync(
        List<TranscriptSegment> segments,
        string sourceLanguage,
        string targetLanguage,
        bool preserveTimestamps = true,
        bool preserveContext = true,
        CancellationToken cancellationToken = default);
}

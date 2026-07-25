namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stub implementation of ITranslationService.
/// </summary>
public class TranslationService(ILogger<TranslationService> logger) : ITranslationService
{
    public async Task<List<TranslatedSegment>> TranslateSegmentsAsync(
        List<TranscriptSegment> segments,
        string sourceLanguage,
        string targetLanguage,
        bool preserveTimestamps = true,
        bool preserveContext = true,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Translating {SegmentCount} segments from {SourceLang} to {TargetLang}",
            segments.Count, sourceLanguage, targetLanguage);

        var translated = segments.Select(s => new TranslatedSegment
        {
            Id = s.Id,
            SegmentNumber = s.SegmentNumber,
            SpeakerId = s.SpeakerId,
            Text = s.Text,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            Confidence = s.Confidence,
            TranslatedText = $"[Translated: {s.Text}]",
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage
        }).ToList();

        await Task.CompletedTask;
        return translated;
    }
}

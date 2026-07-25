using Trackdub.Application.Dubbing;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;

namespace Trackdub.Sdk;

/// <summary>
/// Per-project entry point exposing the workspace workflows.
/// Owns a DI scope and disposes it on close.
/// </summary>
public sealed class TrackdubSession : IDubbingSession
{
    private readonly IDubbingSession _inner;

    internal TrackdubSession(IDubbingSession inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// The root path of the .trackdub project directory (normalized to full path).
    /// </summary>
    public string ProjectRootPath => _inner.ProjectRootPath;

    /// <summary>
    /// The workspace providing access to Project, Transcript, Translation, Tts, Export,
    /// and other pipeline workflows scoped to this session.
    /// </summary>
    public TranscriptWorkspace Workspace => _inner.Workspace;

    /// <inheritdoc />
    public IServiceProvider Services => _inner.Services;

    /// <summary>
    /// Gets the scoped service provider for this session.
    /// </summary>
    internal IServiceProvider? GetServiceProvider() => _inner.Services;

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    internal static StudioSettings? ToStudioSettings(SdkSessionOptions? options) =>
        options is null
            ? null
            : StudioSettings.Default with
            {
                DefaultSourceLanguage = options.DefaultSourceLanguage,
                DefaultTargetLanguage = options.DefaultTargetLanguage,
                ModelTierPreference = options.ModelTierPreference,
                TtsTiming = options.TtsTiming,
                AsrModelOverride = options.AsrModelOverride,
            };
}

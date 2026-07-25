using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Sdk.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk;

/// <summary>
/// Fluent configuration entry point for the headless Trackdub SDK.
/// Collects settings and produces a <see cref="TrackdubSessionFactory"/>.
/// </summary>
public sealed class TrackdubBuilder
{
    private string? _modelDirectory;
    private string? _modelCacheDirectory;
    private string? _logDirectory;
    private ExecutionProviderPreference _executionProvider = ExecutionProviderPreference.Auto;
    private WindowsMlExecutionDevicePolicy _windowsMlExecutionDevicePolicy = WindowsMlExecutionDevicePolicy.Explicit;
    private string? _ffmpegPath;
    private string? _ffprobePath;
    private IApplicationLogger? _logger;
    private Action<IServiceCollection>? _serviceConfigurator;

    /// <summary>
    /// Sets the path to the directory containing downloaded models.
    /// </summary>
    /// <param name="path">Absolute path to the model directory.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithModelDirectory(string path)
    {
        _modelDirectory = path;
        return this;
    }

    /// <summary>
    /// Sets the path to the model cache directory.
    /// </summary>
    /// <param name="path">Absolute path to the model cache directory.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithModelCacheDirectory(string path)
    {
        _modelCacheDirectory = path;
        return this;
    }

    /// <summary>
    /// Sets the path to the log output directory.
    /// </summary>
    /// <param name="path">Absolute path to the log directory.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithLogDirectory(string path)
    {
        _logDirectory = path;
        return this;
    }

    /// <summary>
    /// Sets the preferred execution provider for inference.
    /// </summary>
    /// <param name="preference">The execution provider preference.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithExecutionProvider(ExecutionProviderPreference preference)
    {
        _executionProvider = preference;
        return this;
    }

    /// <summary>
    /// Sets the Windows ML execution-provider device policy (advanced). Windows-only; ignored on
    /// other platforms. <see cref="WindowsMlExecutionDevicePolicy.Explicit"/> (default) keeps
    /// Trackdub's own explicit catalog device selection instead of delegating to ORT's
    /// <c>SetEpSelectionPolicy</c>.
    /// </summary>
    /// <param name="policy">The Windows ML device policy.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithWindowsMlExecutionDevicePolicy(WindowsMlExecutionDevicePolicy policy)
    {
        _windowsMlExecutionDevicePolicy = policy;
        return this;
    }

    /// <summary>
    /// Sets the path to the FFmpeg executable and optionally FFprobe.
    /// </summary>
    /// <param name="ffmpegPath">Path to the FFmpeg executable.</param>
    /// <param name="ffprobePath">Optional path to the FFprobe executable.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithFfmpegPath(string? ffmpegPath, string? ffprobePath = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        return this;
    }

    /// <summary>
    /// Sets a custom application logger to replace the default rolling file logger.
    /// </summary>
    /// <param name="logger">The logger implementation to use.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithLogger(IApplicationLogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Provides a delegate to override or extend DI registrations.
    /// </summary>
    /// <param name="configure">Action invoked during container build to customize services.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceConfigurator = configure;
        return this;
    }

    /// <summary>
    /// Validates configuration and builds a <see cref="TrackdubSessionFactory"/>.
    /// </summary>
    /// <returns>A thread-safe factory for creating dubbing sessions.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when <see cref="WithModelDirectory"/> specifies a path that does not exist.
    /// </exception>
    public TrackdubSessionFactory Build()
    {
        // Validate model directory exists if specified.
        if (_modelDirectory is not null && !Directory.Exists(_modelDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Model directory not found: {_modelDirectory}");
        }

        // Validate FFmpeg path exists if specified.
        if (_ffmpegPath is not null && !File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException(
                $"FFmpeg executable not found: {_ffmpegPath}", _ffmpegPath);
        }

        var options = new TrackdubOptions
        {
            ModelDirectory = _modelDirectory,
            ModelCacheDirectory = _modelCacheDirectory,
            LogDirectory = _logDirectory,
            ExecutionProvider = _executionProvider,
            WindowsMlExecutionDevicePolicy = _windowsMlExecutionDevicePolicy,
            FfmpegPath = _ffmpegPath,
            FfprobePath = _ffprobePath,
            Logger = _logger,
            ServiceConfigurator = _serviceConfigurator,
        };

        var services = new ServiceCollection();
        HeadlessCompositionRoot.AddHeadlessTrackdub(services, options);

        ServiceProvider serviceProvider = services.BuildServiceProvider();

        return new TrackdubSessionFactory(serviceProvider);
    }
}

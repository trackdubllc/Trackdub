using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Application.Tests;

public sealed class RuntimeModelSetupWorkflowTests
{
    [Fact]
    public async Task EnsureModelsAvailableAsync_downloads_missing_model_and_returns_ready()
    {
        var request = new RuntimeModelRequest(RuntimeStage.Asr);
        var bootstrap = new FakeRuntimeModelBootstrapService(CreateStatus(request.Stage, isAvailable: false));
        var workflow = new RuntimeModelWorkflow(bootstrap);
        var decisions = new Queue<RuntimeModelSetupDecision>([RuntimeModelSetupDecision.Download]);
        var busyMessages = new List<string>();

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
            workflow,
            [request],
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: prompt =>
                {
                    Assert.False(prompt.IsRetry);
                    Assert.Equal(RuntimeStage.Asr, prompt.Request.Stage);
                    return Task.FromResult(decisions.Dequeue());
                },
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: async (operation, busyMessage) =>
                {
                    busyMessages.Add(busyMessage);
                    await operation(TestContext.Current.CancellationToken);
                }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Empty(result.SkippedStages);
        Assert.Equal(1, bootstrap.DownloadCount);
        Assert.Equal(["Downloading transcription model..."], busyMessages);
    }

    [Fact]
    public async Task GetRequiredModelStatusAsync_returns_ready_for_deepl_cloud_translation_without_model_bootstrap()
    {
        var request = new RuntimeModelRequest(
            RuntimeStage.Translation,
            PreferredModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
            SourceLanguage: "en",
            TargetLanguage: "es",
            RequirePreferredModelAlias: true);
        var bootstrap = new FakeRuntimeModelBootstrapService(CreateStatus(request.Stage, isAvailable: false));
        var workflow = new RuntimeModelWorkflow(bootstrap);

        RequiredRuntimeModelStatus? status = await workflow.GetRequiredModelStatusAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Null(status);
        Assert.Equal(0, bootstrap.StatusCheckCount);
    }

    [Fact]
    public async Task EnsureModelsAvailableAsync_returns_skipped_stage_for_optional_separation()
    {
        var request = new RuntimeModelRequest(RuntimeStage.Separation);
        var bootstrap = new FakeRuntimeModelBootstrapService(CreateStatus(request.Stage, isAvailable: false));
        var workflow = new RuntimeModelWorkflow(bootstrap);

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
            workflow,
            [request],
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: prompt =>
                {
                    Assert.True(prompt.CanSkipOptionalStage);
                    return Task.FromResult(RuntimeModelSetupDecision.SkipOptionalStage);
                },
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            allowOptionalStageSkip: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Equal([RuntimeStage.Separation], result.SkippedStages);
        Assert.Equal(0, bootstrap.DownloadCount);
        Assert.Equal(0, bootstrap.ImportCount);
    }

    [Fact]
    public async Task EnsureModelsAvailableAsync_imports_selected_file_when_download_is_unavailable()
    {
        var request = new RuntimeModelRequest(RuntimeStage.Tts);
        var bootstrap = new FakeRuntimeModelBootstrapService(CreateStatus(request.Stage, isAvailable: false, canAutoDownload: false));
        var workflow = new RuntimeModelWorkflow(bootstrap);

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
            workflow,
            [request],
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: _ => Task.FromResult(RuntimeModelSetupDecision.Import),
                PickImportFileAsync: () => Task.FromResult<string?>(@"D:\models\tts.onnx"),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Equal(1, bootstrap.ImportCount);
        Assert.Equal(@"D:\models\tts.onnx", bootstrap.ImportedPath);
    }

    [Fact]
    public async Task EnsureModelsAvailableAsync_can_retry_auto_download_after_failed_setup()
    {
        var request = new RuntimeModelRequest(RuntimeStage.Separation);
        var bootstrap = new FakeRuntimeModelBootstrapService(
            CreateStatus(request.Stage, isAvailable: false) with { FailureReason = "Download failed." },
            failedDownloadsBeforeReady: 1);
        var workflow = new RuntimeModelWorkflow(bootstrap);
        var prompts = new List<RuntimeModelSetupPrompt>();

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
            workflow,
            [request],
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: prompt =>
                {
                    prompts.Add(prompt);
                    return Task.FromResult(RuntimeModelSetupDecision.Download);
                },
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Equal(2, bootstrap.DownloadCount);
        Assert.Collection(
            prompts,
            prompt => Assert.False(prompt.IsRetry),
            prompt => Assert.True(prompt.IsRetry));
    }

    [Fact]
    public async Task EnsureModelsAvailableAsync_preserves_download_failure_reason_on_retry_prompt()
    {
        var request = new RuntimeModelRequest(RuntimeStage.Separation);
        RequiredRuntimeModelStatus genericMissingStatus = CreateStatus(request.Stage, isAvailable: false) with
        {
            FailureReason = "The Hush native runtime file 'deployment/lib/weya_nc.dll' is missing."
        };
        RequiredRuntimeModelStatus failedDownloadStatus = genericMissingStatus with
        {
            FailureReason = "Failed to download 'deployment/lib/weya_nc.dll' from 'https://example.test/hush/weya_nc.dll'."
        };
        var bootstrap = new FakeRuntimeModelBootstrapService(
            genericMissingStatus,
            failedDownloadsBeforeReady: 1,
            statusAfterFailedDownload: failedDownloadStatus);
        var workflow = new RuntimeModelWorkflow(bootstrap);
        var prompts = new List<RuntimeModelSetupPrompt>();
        var decisions = new Queue<RuntimeModelSetupDecision>([
            RuntimeModelSetupDecision.Download,
            RuntimeModelSetupDecision.Cancel
        ]);

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
            workflow,
            [request],
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: prompt =>
                {
                    prompts.Add(prompt);
                    return Task.FromResult(decisions.Dequeue());
                },
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsReady);
        Assert.Equal(1, bootstrap.DownloadCount);
        Assert.Collection(
            prompts,
            prompt => Assert.False(prompt.IsRetry),
            prompt =>
            {
                Assert.True(prompt.IsRetry);
                Assert.Equal(failedDownloadStatus.FailureReason, prompt.Status.FailureReason);
            });
    }

    [Fact]
    public async Task EnsureModelsAvailableAsync_reports_downloaded_but_unresolved_optional_stage_as_skipped()
    {
        var request = new RuntimeModelRequest(RuntimeStage.Separation);
        var bootstrap = new FakeRuntimeModelBootstrapService(
            CreateStatus(request.Stage, isAvailable: false) with
            {
                CanAutoDownload = true,
                CanImportSingleFile = false,
                FailureReason = "Model bundle missing."
            },
            statusAfterSuccessfulDownload: CreateStatus(request.Stage, isAvailable: false) with
            {
                CanAutoDownload = false,
                CanImportSingleFile = false,
                FailureReason = "The Hush native runtime file 'deployment/lib/weya_nc.dll' is missing."
            });
        var workflow = new RuntimeModelWorkflow(bootstrap);
        var decisions = new Queue<RuntimeModelSetupDecision>([
            RuntimeModelSetupDecision.Download,
            RuntimeModelSetupDecision.SkipOptionalStage
        ]);

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
            workflow,
            [request],
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: _ => Task.FromResult(decisions.Dequeue()),
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            allowOptionalStageSkip: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Equal([RuntimeStage.Separation], result.SkippedStages);
        Assert.Equal(1, bootstrap.DownloadCount);
    }

    [Fact]
    public void EnsureModelsAvailableAsync_invokes_decision_callback_on_captured_context_after_async_status_check()
    {
        var request = new RuntimeModelRequest(RuntimeStage.Tts);
        var bootstrap = new FakeRuntimeModelBootstrapService(
            CreateStatus(request.Stage, isAvailable: false),
            completeStatusAsynchronously: true);
        var workflow = new RuntimeModelWorkflow(bootstrap);
        using var context = new PumpingSynchronizationContext();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext? callbackContext = null;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Task<RuntimeModelSetupResult> task = RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
                workflow,
                [request],
                new RuntimeModelSetupCallbacks(
                    ResolveDecisionAsync: _ =>
                    {
                        callbackContext = SynchronizationContext.Current;
                        return Task.FromResult(RuntimeModelSetupDecision.Cancel);
                    },
                    PickImportFileAsync: () => Task.FromResult<string?>(null),
                    CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                    RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);

            RuntimeModelSetupResult result = context.PumpUntilCompleted(task);

            Assert.False(result.IsReady);
            Assert.Same(context, callbackContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task EnsureManifestCompanionModelsAvailableAsync_skips_download_when_companions_are_ready()
    {
        var bootstrap = new FakeRuntimeModelBootstrapService(CreateStatus(RuntimeStage.LipSynthesis, isAvailable: true));
        var workflow = new RuntimeModelWorkflow(bootstrap);

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureManifestCompanionModelsAvailableAsync(
            workflow,
            ["InsightFace/scrfd-500m", "InsightFace/2d106det"],
            RuntimeStage.LipSynthesis,
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: _ => Task.FromResult(RuntimeModelSetupDecision.Download),
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Equal(0, bootstrap.DownloadCount);
    }

    [Fact]
    public async Task EnsureManifestCompanionModelsAvailableAsync_downloads_missing_companion_models()
    {
        var bootstrap = new FakeRuntimeModelBootstrapService(
            CreateStatus(RuntimeStage.LipSynthesis, isAvailable: false),
            statusAfterSuccessfulDownload: CreateStatus(RuntimeStage.LipSynthesis, isAvailable: true));
        var workflow = new RuntimeModelWorkflow(bootstrap);

        RuntimeModelSetupResult result = await RuntimeModelSetupWorkflow.EnsureManifestCompanionModelsAvailableAsync(
            workflow,
            ["InsightFace/scrfd-500m"],
            RuntimeStage.LipSynthesis,
            new RuntimeModelSetupCallbacks(
                ResolveDecisionAsync: _ => Task.FromResult(RuntimeModelSetupDecision.Download),
                PickImportFileAsync: () => Task.FromResult<string?>(null),
                CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
                RunOperationAsync: (operation, _) => operation(TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Equal(1, bootstrap.DownloadCount);
    }

    private static RequiredRuntimeModelStatus CreateStatus(
        RuntimeStage stage,
        bool isAvailable,
        bool canAutoDownload = true) =>
        new(
            stage,
            StageDisplayName: stage switch
            {
                RuntimeStage.Asr => "Transcription",
                RuntimeStage.Tts => "Speech synthesis",
                _ => stage.ToString()
            },
            ModelId: "test/model",
            ModelAlias: null,
            Variant: "default",
            ExpectedFileName: "model.onnx",
            ModelPath: @"D:\models\model.onnx",
            SourceUrl: "https://example.test/model",
            License: "MIT",
            IsAvailable: isAvailable,
            CanAutoDownload: canAutoDownload,
            CanImportSingleFile: true,
            RequiresAttribution: false,
            RequiresUserConsent: false,
            HelpText: "Model required.");

    private sealed class FakeRuntimeModelBootstrapService(
        RequiredRuntimeModelStatus status,
        bool completeStatusAsynchronously = false,
        int failedDownloadsBeforeReady = 0,
        RequiredRuntimeModelStatus? statusAfterSuccessfulDownload = null,
        RequiredRuntimeModelStatus? statusAfterFailedDownload = null)
        : IRuntimeModelBootstrapService
    {
        private RequiredRuntimeModelStatus status = status;

        public int DownloadCount { get; private set; }

        public int ImportCount { get; private set; }

        public int StatusCheckCount { get; private set; }

        public string? ImportedPath { get; private set; }

        public Task<RequiredRuntimeModelStatus?> GetRequiredModelStatusAsync(
            RuntimeModelRequest request,
            CancellationToken cancellationToken = default)
        {
            StatusCheckCount++;
            return completeStatusAsynchronously
                ? Task.Run<RequiredRuntimeModelStatus?>(() => status, cancellationToken)
                : Task.FromResult<RequiredRuntimeModelStatus?>(status);
        }

        public Task<RequiredRuntimeModelStatus> DownloadRequiredModelAsync(
            RuntimeModelRequest request,
            IProgress<ModelDownloadProgress>? downloadProgress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            if (DownloadCount > failedDownloadsBeforeReady)
            {
                status = statusAfterSuccessfulDownload ?? status with { IsAvailable = true };
            }
            else if (statusAfterFailedDownload is not null)
            {
                return Task.FromResult(statusAfterFailedDownload);
            }

            return Task.FromResult(status);
        }

        public Task<RequiredRuntimeModelStatus> ImportRequiredModelAsync(
            RuntimeModelRequest request,
            string sourceModelPath,
            CancellationToken cancellationToken = default)
        {
            ImportCount++;
            ImportedPath = sourceModelPath;
            status = status with { IsAvailable = true };
            return Task.FromResult(status);
        }

        public Task<RequiredRuntimeModelStatus?> GetManifestCompanionModelStatusAsync(
            string manifestAlias,
            RuntimeStage owningStage,
            CancellationToken cancellationToken = default) =>
            GetRequiredModelStatusAsync(new RuntimeModelRequest(owningStage, PreferredModelAlias: manifestAlias), cancellationToken);

        public Task<RequiredRuntimeModelStatus> DownloadManifestCompanionModelAsync(
            string manifestAlias,
            RuntimeStage owningStage,
            IProgress<ModelDownloadProgress>? downloadProgress = null,
            CancellationToken cancellationToken = default) =>
            DownloadRequiredModelAsync(new RuntimeModelRequest(owningStage, PreferredModelAlias: manifestAlias), downloadProgress, cancellationToken);
    }

    private sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> workItems = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            workItems.Add((d, state));
        }

        public T PumpUntilCompleted<T>(Task<T> task)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!task.IsCompleted)
            {
                if (!workItems.TryTake(out (SendOrPostCallback Callback, object? State) item, TimeSpan.FromMilliseconds(50)))
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        throw new TimeoutException("Timed out waiting for the test synchronization context to receive work.");
                    }

                    continue;
                }

                SynchronizationContext? previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    item.Callback(item.State);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }

            return task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            workItems.Dispose();
        }
    }
}

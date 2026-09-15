using System.Runtime.CompilerServices;
using Trackdub.Contracts;
using Trackdub.Contracts.ModelOptimization;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.ModelOptimization;

public sealed class OliveModelOptimizationService : IModelOptimizationService
{
    private readonly IOliveEnvironmentService _oliveEnvironment;
    private readonly IAppStoragePaths _storagePaths;
    private readonly IStreamingProcessRunner _runner;
    private readonly IModelVariantRegistrar _variantRegistrar;

    public OliveModelOptimizationService(
        IOliveEnvironmentService oliveEnvironment,
        IAppStoragePaths storagePaths,
        IModelVariantRegistrar variantRegistrar)
        : this(oliveEnvironment, storagePaths, new StreamingProcessRunner(), variantRegistrar)
    {
    }

    internal OliveModelOptimizationService(
        IOliveEnvironmentService oliveEnvironment,
        IAppStoragePaths storagePaths,
        IStreamingProcessRunner runner,
        IModelVariantRegistrar variantRegistrar)
    {
        ArgumentNullException.ThrowIfNull(oliveEnvironment);
        ArgumentNullException.ThrowIfNull(storagePaths);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(variantRegistrar);
        _oliveEnvironment = oliveEnvironment;
        _storagePaths = storagePaths;
        _runner = runner;
        _variantRegistrar = variantRegistrar;
    }

    public async IAsyncEnumerable<string> OptimizeAsync(
        ModelOptimizationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.OliveRecipeConfigPath))
        {
            await foreach (string line in OptimizeWithRecipeAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }

            yield break;
        }

        if (string.Equals(request.OliveMode, "ort-genai-builder", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (string line in OliveGenAiBuilderOptimizeAsync(request, cancellationToken).ConfigureAwait(false))
                yield return line;
            yield break;
        }

        if (request.ComponentRelativePaths.Count == 0)
        {
            throw new InvalidOperationException(
                $"No ONNX components were declared for '{request.ModelId}'.");
        }

        string[] componentPaths = request.ComponentRelativePaths
            .Select(path => ResolveModelComponentPath(request.ModelRootPath, path))
            .ToArray();
        string variantAlias = string.IsNullOrWhiteSpace(request.VariantAlias)
            ? BuildVariantAlias(request.ExecutionProvider, request.Precision)
            : request.VariantAlias;
        string entryRelativePath = string.IsNullOrWhiteSpace(request.EntryRelativePath)
            ? request.ComponentRelativePaths[0]
            : request.EntryRelativePath;
        _ = ResolveRelativePathUnderRoot(request.ModelRootPath, entryRelativePath);

        string tempOutputPath = request.OutputVariantPath + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(tempOutputPath);

        string oliveExe = _oliveEnvironment.GetOliveExecutablePath(request.ExecutionProvider);
        string cacheRoot = GetOliveCacheRoot(request.ModelId);

        string oliveDevice = ToOliveDevice(request.ExecutionProvider);
        string oliveProvider = ToOliveProvider(request.ExecutionProvider);
        var oliveWorkDirectories = new List<string>();

        try
        {
            foreach ((string componentPath, string relativePath) in componentPaths.Zip(request.ComponentRelativePaths))
            {
                string componentName = Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '_').Replace('/', '_'));
                string oliveOutputDir = CreateUniqueOliveWorkDirectory(cacheRoot, componentName);
                oliveWorkDirectories.Add(oliveOutputDir);
                Directory.CreateDirectory(oliveOutputDir);

                yield return $"Optimizing {relativePath} [{oliveProvider}]...";

                await foreach (string line in RunOliveOptimizeAsync(
                    oliveExe,
                    componentPath,
                    oliveOutputDir,
                    oliveDevice,
                    oliveProvider,
                    request.Precision,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return line;
                }

                string optimizedModelPath = Path.Combine(oliveOutputDir, "model.onnx");
                if (!File.Exists(optimizedModelPath))
                {
                    throw new InvalidOperationException(
                        $"Olive did not produce 'model.onnx' for '{relativePath}'.");
                }

                string targetPath = ResolveOutputComponentPath(tempOutputPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(optimizedModelPath, targetPath, overwrite: true);

                string sourceDataPath = Path.Combine(oliveOutputDir, "model.onnx.data");
                string targetDataPath = targetPath + ".data";
                if (File.Exists(sourceDataPath))
                {
                    File.Copy(sourceDataPath, targetDataPath, overwrite: true);
                }

                yield return $"  ready: {relativePath}";
            }

            var registration = new ModelOptimizedVariantRegistration(
                ModelId: request.ModelId,
                BaseModelRootPath: request.ModelRootPath,
                VariantAlias: variantAlias,
                VariantRootPath: request.OutputVariantPath,
                EntryRelativePath: entryRelativePath,
                ComponentRelativePaths: request.ComponentRelativePaths,
                OptimizerId: "olive",
                ExecutionProvider: ToExecutionProviderKind(request.ExecutionProvider),
                Precision: request.Precision,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Provenance: BuildProvenance(request));
            await CommitOutputDirectoryAndRegisterAsync(
                tempOutputPath,
                request.OutputVariantPath,
                registration,
                cancellationToken).ConfigureAwait(false);
            yield return $"Registered optimized variant: {variantAlias}";
            yield return "Optimization complete.";
        }
        finally
        {
            CleanupOliveWorkDirectories(oliveWorkDirectories, tempOutputPath);
        }
    }

    private string GetOliveCacheRoot(string modelId) =>
        Path.Combine(
            _storagePaths.ToolCacheDirectory,
            "olive-cache",
            modelId.Replace('/', '_').Replace('\\', '_'));

    private static string CreateUniqueOliveWorkDirectory(string cacheRoot, string prefix) =>
        Path.Combine(cacheRoot, $"{prefix}-{Guid.NewGuid():N}");

    private static void CleanupOliveWorkDirectories(
        IReadOnlyList<string> oliveWorkDirectories,
        string? tempOutputPath = null)
    {
        DeleteDirectoryIfExists(tempOutputPath);
        foreach (string workDirectory in oliveWorkDirectories)
        {
            DeleteDirectoryIfExists(workDirectory);
        }
    }

    private static void DeleteDirectoryIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of ephemeral Olive working directories.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of ephemeral Olive working directories.
        }
    }

    private static string ResolveModelComponentPath(string modelRootPath, string componentRelativePath)
    {
        string fullPath = ResolveRelativePathUnderRoot(modelRootPath, componentRelativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Optimization component '{componentRelativePath}' was not found.", fullPath);
        }

        return fullPath;
    }

    private static string ResolveOutputComponentPath(string outputRootPath, string componentRelativePath) =>
        ResolveRelativePathUnderRoot(outputRootPath, componentRelativePath);

    private static string ResolveRelativePathUnderRoot(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path must not be empty.", nameof(rootPath));
        }

        if (string.IsNullOrWhiteSpace(relativePath) || IsRootedLikePath(relativePath))
        {
            throw new InvalidOperationException($"Optimization component path is invalid: {relativePath}.");
        }

        string normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            throw new InvalidOperationException($"Optimization component path is invalid: {relativePath}.");
        }

        if (!normalized.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Optimization component path must reference an ONNX file: {relativePath}.");
        }

        string fullRoot = Path.GetFullPath(rootPath);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsStrictSubpathOrEqual(fullPath, fullRoot))
        {
            throw new InvalidOperationException($"Optimization component path is invalid: {relativePath}.");
        }

        return fullPath;
    }

    private async Task CommitOutputDirectoryAndRegisterAsync(
        string tempOutputPath,
        string outputVariantPath,
        ModelOptimizedVariantRegistration registration,
        CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(outputVariantPath));
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string? backupPath = null;
        if (Directory.Exists(outputVariantPath))
        {
            backupPath = outputVariantPath + $".bak-{Guid.NewGuid():N}";
            Directory.Move(outputVariantPath, backupPath);
        }

        try
        {
            Directory.Move(tempOutputPath, outputVariantPath);
            await _variantRegistrar.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
            if (backupPath is not null && Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }
        }
        catch
        {
            if (Directory.Exists(outputVariantPath))
            {
                Directory.Delete(outputVariantPath, recursive: true);
            }

            if (backupPath is not null &&
                Directory.Exists(backupPath) &&
                !Directory.Exists(outputVariantPath))
            {
                Directory.Move(backupPath, outputVariantPath);
            }

            throw;
        }
    }

    private static string ResolveGenAiModelInputPath(string modelRootPath, string? entryRelativePath, string configFileName)
    {
        if (string.IsNullOrWhiteSpace(entryRelativePath) ||
            !entryRelativePath.EndsWith(configFileName, StringComparison.OrdinalIgnoreCase))
        {
            return modelRootPath;
        }

        if (IsRootedLikePath(entryRelativePath))
        {
            throw new InvalidOperationException($"GenAI entry path must be relative: '{entryRelativePath}'.");
        }

        string normalized = entryRelativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            throw new InvalidOperationException($"GenAI entry path is invalid: '{entryRelativePath}'.");
        }

        string fullRoot = Path.GetFullPath(modelRootPath);
        string fullConfig = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsStrictSubpathOrEqual(fullConfig, fullRoot))
        {
            throw new InvalidOperationException($"GenAI entry path escapes model root: '{entryRelativePath}'.");
        }

        return Path.GetDirectoryName(fullConfig) ?? fullRoot;
    }

    private static bool IsStrictSubpathOrEqual(string path, string ancestor)
    {
        string ancestorFull = Path.GetFullPath(ancestor);
        string pathFull = Path.GetFullPath(path);
        string prefix = AppendDirectorySeparator(ancestorFull);
        return pathFull.Equals(Path.TrimEndingDirectorySeparator(ancestorFull), StringComparison.OrdinalIgnoreCase)
            || pathFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootedLikePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return Path.IsPathRooted(path) ||
            normalized.StartsWith('/') ||
            (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':');
    }

    private static string AppendDirectorySeparator(string path)
    {
        string full = Path.GetFullPath(path);
        char sep = Path.DirectorySeparatorChar;
        return full.EndsWith(sep) ? full : full + sep;
    }

    private static string ToOliveDevice(OliveExecutionProvider provider) =>
        provider switch
        {
            OliveExecutionProvider.Cpu => "cpu",
            OliveExecutionProvider.Qnn or OliveExecutionProvider.VitisAi => "npu",
            _ => "gpu"
        };

    private static string ToOliveProvider(OliveExecutionProvider provider) =>
        provider switch
        {
            OliveExecutionProvider.Dml => "DmlExecutionProvider",
            OliveExecutionProvider.Cuda => "CUDAExecutionProvider",
            OliveExecutionProvider.TensorRt => "TensorrtExecutionProvider",
            OliveExecutionProvider.TensorRtRtx => "NvTensorRTRTXExecutionProvider",
            OliveExecutionProvider.Migraphx => "MIGraphXExecutionProvider",
            OliveExecutionProvider.Rocm => "ROCMExecutionProvider",
            OliveExecutionProvider.VitisAi => "VitisAIExecutionProvider",
            OliveExecutionProvider.Qnn => "QNNExecutionProvider",
            OliveExecutionProvider.OpenVino => "OpenVINOExecutionProvider",
            _ => "CPUExecutionProvider"
        };

    private static string BuildVariantAlias(OliveExecutionProvider provider, string precision) =>
        $"olive-{provider.ToString().ToLowerInvariant()}-{precision}";

    private static ModelOptimizedVariantProvenance BuildProvenance(ModelOptimizationRequest request) =>
        new(
            OliveVersion: null,
            CommandKind: request.OliveRecipeConfigPath is null ? "optimize" : "run --config",
            Operations: request.Operations,
            OliveProvider: ToOliveProvider(request.ExecutionProvider),
            Device: ToOliveDevice(request.ExecutionProvider),
            RecipeConfigPath: request.OliveRecipeConfigPath,
            RecipeConfigSha256: request.RecipeConfigHash,
            QuantizationMethod: request.QuantizationMethod,
            Evaluator: null,
            OutputKind: request.ExpectedOutput,
            FallbackPolicy: request.FallbackPolicy);

    private static ExecutionProviderKind ToExecutionProviderKind(OliveExecutionProvider provider) =>
        provider switch
        {
            OliveExecutionProvider.Dml => ExecutionProviderKind.DirectMl,
            OliveExecutionProvider.Cuda => ExecutionProviderKind.Cuda,
            OliveExecutionProvider.TensorRt => ExecutionProviderKind.TensorRt,
            OliveExecutionProvider.TensorRtRtx => ExecutionProviderKind.TensorRTRtx,
            OliveExecutionProvider.Migraphx or OliveExecutionProvider.Rocm => ExecutionProviderKind.Migraphx,
            OliveExecutionProvider.Qnn => ExecutionProviderKind.Qnn,
            OliveExecutionProvider.OpenVino => ExecutionProviderKind.OpenVinoCatalog,
            OliveExecutionProvider.VitisAi => ExecutionProviderKind.VitisAi,
            _ => ExecutionProviderKind.Cpu
        };

    private async IAsyncEnumerable<string> OliveGenAiBuilderOptimizeAsync(
        ModelOptimizationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const string GenAiConfigFileName = "genai_config.json";

        string modelInputPath = ResolveGenAiModelInputPath(
            request.ModelRootPath, request.EntryRelativePath, GenAiConfigFileName);
        string genAiConfigPath = Path.Combine(modelInputPath, GenAiConfigFileName);
        if (!File.Exists(genAiConfigPath))
        {
            throw new InvalidOperationException(
                $"GenAI model root '{request.ModelRootPath}' does not contain {GenAiConfigFileName}.");
        }

        string[] topLevelOnnxRelativePaths = Directory
            .EnumerateFiles(modelInputPath, "*.onnx", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(request.ModelRootPath, path).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (topLevelOnnxRelativePaths.Length > 1)
        {
            // The model folder already contains multiple exported ONNX graphs (e.g. a Whisper GenAI
            // bundle's encoder + decoder). These must be optimized per-component: Olive's whole-folder
            // model-builder path (optimize against the directory) aborts with
            // "Found multiple .onnx model files. Please specify one." because it cannot pick a single
            // input model. Per-component optimize feeds Olive one graph at a time and reassembles the
            // bundle around the original genai_config.json.
            await foreach (string line in OliveGenAiMultiOnnxOptimizeAsync(
                request,
                GenAiConfigFileName,
                ResolveGenAiOnnxComponents(request, topLevelOnnxRelativePaths),
                cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }

            yield break;
        }

        if (topLevelOnnxRelativePaths.Length == 1 &&
            string.Equals(modelInputPath, request.ModelRootPath, StringComparison.OrdinalIgnoreCase))
        {
            await foreach (string line in OliveGenAiSingleOnnxOptimizeAsync(
                request,
                GenAiConfigFileName,
                ResolveModelComponentPath(request.ModelRootPath, topLevelOnnxRelativePaths[0]),
                cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }

            yield break;
        }

        await foreach (string line in OliveGenAiBundledFolderOptimizeAsync(
            request,
            GenAiConfigFileName,
            cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    private static string[] ResolveGenAiOnnxComponents(ModelOptimizationRequest request, string[] topLevelOnnxFiles)
    {
        if (topLevelOnnxFiles.Length > 1)
        {
            return topLevelOnnxFiles;
        }

        string[] declared = request.ComponentRelativePaths
            .Where(path => path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.Replace('\\', '/'))
            .ToArray();

        if (declared.Length > 0)
        {
            return declared;
        }

        if (!string.IsNullOrWhiteSpace(request.EntryRelativePath) &&
            request.EntryRelativePath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            return [request.EntryRelativePath.Replace('\\', '/')];
        }

        return topLevelOnnxFiles;
    }

    private async IAsyncEnumerable<string> OliveGenAiMultiOnnxOptimizeAsync(
        ModelOptimizationRequest request,
        string genAiConfigFileName,
        IReadOnlyList<string> onnxComponents,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (onnxComponents.Count == 0)
        {
            throw new InvalidOperationException(
                $"No ONNX components were declared for GenAI optimization of '{request.ModelId}'.");
        }

        string variantAlias = string.IsNullOrWhiteSpace(request.VariantAlias)
            ? BuildVariantAlias(request.ExecutionProvider, request.Precision)
            : request.VariantAlias;
        string tempOutputPath = request.OutputVariantPath + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(tempOutputPath);

        string oliveExe = _oliveEnvironment.GetOliveExecutablePath(request.ExecutionProvider);
        string cacheRoot = GetOliveCacheRoot(request.ModelId);
        string oliveDevice = ToOliveDevice(request.ExecutionProvider);
        string oliveProvider = ToOliveProvider(request.ExecutionProvider);
        var optimizedOnnx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oliveWorkDirectories = new List<string>();

        try
        {
            foreach (string relativeOnnxPath in onnxComponents)
            {
                string componentPath = ResolveModelComponentPath(request.ModelRootPath, relativeOnnxPath);
                string componentName = Path.GetFileNameWithoutExtension(relativeOnnxPath.Replace('\\', '_').Replace('/', '_'));
                string oliveOutputDir = CreateUniqueOliveWorkDirectory(cacheRoot, componentName);
                oliveWorkDirectories.Add(oliveOutputDir);
                Directory.CreateDirectory(oliveOutputDir);

                yield return $"Optimizing {relativeOnnxPath} [{oliveProvider}]...";

                await foreach (string line in RunOliveOptimizeAsync(
                    oliveExe,
                    componentPath,
                    oliveOutputDir,
                    oliveDevice,
                    oliveProvider,
                    request.Precision,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return line;
                }

                string optimizedModelPath = Path.Combine(oliveOutputDir, "model.onnx");
                if (!File.Exists(optimizedModelPath))
                {
                    throw new InvalidOperationException(
                        $"Olive did not produce 'model.onnx' for '{relativeOnnxPath}'.");
                }

                string targetPath = ResolveOutputComponentPath(tempOutputPath, relativeOnnxPath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(optimizedModelPath, targetPath, overwrite: true);

                string sourceDataPath = Path.Combine(oliveOutputDir, "model.onnx.data");
                string targetDataPath = targetPath + ".data";
                if (File.Exists(sourceDataPath))
                {
                    File.Copy(sourceDataPath, targetDataPath, overwrite: true);
                }

                optimizedOnnx.Add(relativeOnnxPath.Replace('\\', '/'));
                yield return $"  ready: {relativeOnnxPath}";
            }

            string companionSourcePath = ResolveGenAiModelInputPath(
                request.ModelRootPath, request.EntryRelativePath, genAiConfigFileName);
            CopyGenAiCompanionFiles(companionSourcePath, tempOutputPath, optimizedOnnx);

            string[] componentPaths = [genAiConfigFileName, .. optimizedOnnx.Order(StringComparer.OrdinalIgnoreCase)];
            var registration = new ModelOptimizedVariantRegistration(
                ModelId: request.ModelId,
                BaseModelRootPath: request.ModelRootPath,
                VariantAlias: variantAlias,
                VariantRootPath: request.OutputVariantPath,
                EntryRelativePath: genAiConfigFileName,
                ComponentRelativePaths: componentPaths,
                OptimizerId: "olive",
                ExecutionProvider: ToExecutionProviderKind(request.ExecutionProvider),
                Precision: request.Precision,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Provenance: BuildProvenance(request));

            await CommitOutputDirectoryAndRegisterAsync(
                tempOutputPath,
                request.OutputVariantPath,
                registration,
                cancellationToken).ConfigureAwait(false);

            yield return $"Registered optimized variant: {variantAlias}";
            yield return "Optimization complete.";
        }
        finally
        {
            CleanupOliveWorkDirectories(oliveWorkDirectories, tempOutputPath);
        }
    }

    private async IAsyncEnumerable<string> OliveGenAiSingleOnnxOptimizeAsync(
        ModelOptimizationRequest request,
        string genAiConfigFileName,
        string onnxComponentPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string relativeOnnxPath = Path.GetRelativePath(request.ModelRootPath, onnxComponentPath).Replace('\\', '/');
        await foreach (string line in OliveGenAiMultiOnnxOptimizeAsync(
            request,
            genAiConfigFileName,
            [relativeOnnxPath],
            cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    private async IAsyncEnumerable<string> OliveGenAiBundledFolderOptimizeAsync(
        ModelOptimizationRequest request,
        string genAiConfigFileName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string modelInputPath = ResolveGenAiModelInputPath(
            request.ModelRootPath, request.EntryRelativePath, genAiConfigFileName);
        string sourceConfigPath = Path.Combine(modelInputPath, genAiConfigFileName);

        if (!File.Exists(sourceConfigPath))
        {
            throw new InvalidOperationException(
                $"GenAI model root '{request.ModelRootPath}' does not contain {genAiConfigFileName}.");
        }

        string variantAlias = string.IsNullOrWhiteSpace(request.VariantAlias)
            ? BuildVariantAlias(request.ExecutionProvider, request.Precision)
            : request.VariantAlias;

        string tempOutputPath = request.OutputVariantPath + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(tempOutputPath);

        string oliveExe = _oliveEnvironment.GetOliveExecutablePath(request.ExecutionProvider);
        string cacheRoot = GetOliveCacheRoot(request.ModelId);
        string oliveOutputDir = CreateUniqueOliveWorkDirectory(cacheRoot, "genai-builder");
        Directory.CreateDirectory(oliveOutputDir);

        string oliveDevice = ToOliveDevice(request.ExecutionProvider);
        string oliveProvider = ToOliveProvider(request.ExecutionProvider);

        try
        {
            yield return $"Optimizing GenAI model bundle [{oliveProvider}]...";

            await foreach (string line in RunOliveOptimizeAsync(
                oliveExe,
                modelInputPath,
                oliveOutputDir,
                oliveDevice,
                oliveProvider,
                request.Precision,
                cancellationToken,
                useModelBuilder: true).ConfigureAwait(false))
            {
                yield return line;
            }

            string outputModelDir = oliveOutputDir;
            if (!File.Exists(Path.Combine(oliveOutputDir, genAiConfigFileName)))
            {
                string? nested = Directory
                    .EnumerateDirectories(oliveOutputDir)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, genAiConfigFileName)));
                if (nested is null)
                {
                    throw new InvalidOperationException(
                        $"Olive did not produce '{genAiConfigFileName}' in '{oliveOutputDir}'.");
                }

                outputModelDir = nested;
            }

            foreach (string sourceFile in Directory.EnumerateFiles(outputModelDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(outputModelDir, sourceFile);
                string destFile = Path.Combine(tempOutputPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(sourceFile, destFile, overwrite: true);
            }

            CopyGenAiCompanionFiles(modelInputPath, tempOutputPath, optimizedOnnx: null);

            var registration = new ModelOptimizedVariantRegistration(
                ModelId: request.ModelId,
                BaseModelRootPath: request.ModelRootPath,
                VariantAlias: variantAlias,
                VariantRootPath: request.OutputVariantPath,
                EntryRelativePath: genAiConfigFileName,
                ComponentRelativePaths: [genAiConfigFileName],
                OptimizerId: "olive",
                ExecutionProvider: ToExecutionProviderKind(request.ExecutionProvider),
                Precision: request.Precision,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Provenance: BuildProvenance(request));

            await CommitOutputDirectoryAndRegisterAsync(
                tempOutputPath,
                request.OutputVariantPath,
                registration,
                cancellationToken).ConfigureAwait(false);

            yield return $"Registered optimized variant: {variantAlias}";
            yield return "Optimization complete.";
        }
        finally
        {
            CleanupOliveWorkDirectories([oliveOutputDir], tempOutputPath);
        }
    }

    private static void CopyGenAiCompanionFiles(
        string modelRootPath,
        string tempOutputPath,
        IReadOnlySet<string>? optimizedOnnx)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(modelRootPath, "*", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(sourceFile);
            string ext = Path.GetExtension(sourceFile);
            if (string.Equals(ext, ".onnx", StringComparison.OrdinalIgnoreCase))
            {
                if (optimizedOnnx is not null && optimizedOnnx.Contains(fileName))
                {
                    continue;
                }

                string destOnnx = Path.Combine(tempOutputPath, fileName);
                File.Copy(sourceFile, destOnnx, overwrite: true);

                string sourceData = sourceFile + ".data";
                if (File.Exists(sourceData))
                {
                    File.Copy(sourceData, destOnnx + ".data", overwrite: true);
                }

                continue;
            }

            if (string.Equals(ext, ".data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destFile = Path.Combine(tempOutputPath, fileName);
            if (!File.Exists(destFile))
            {
                File.Copy(sourceFile, destFile, overwrite: true);
            }
        }
    }

    private async IAsyncEnumerable<string> RunOliveOptimizeAsync(
        string oliveExe,
        string modelNameOrPath,
        string oliveOutputDir,
        string oliveDevice,
        string oliveProvider,
        string precision,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool useModelBuilder = false)
    {
        List<string> args =
        [
            "optimize",
            "--model_name_or_path", modelNameOrPath,
            "--output_path", oliveOutputDir,
            "--device", oliveDevice,
            "--provider", oliveProvider,
            "--precision", precision,
            "--log_level", "1"
        ];

        if (useModelBuilder)
        {
            args.Add("--exporter");
            args.Add("model_builder");
        }

        await foreach (string line in RunOliveProcessAsync(
            oliveExe,
            [.. args],
            oliveOutputDir,
            cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    private async IAsyncEnumerable<string> OptimizeWithRecipeAsync(
        ModelOptimizationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string recipeConfigPath = Path.GetFullPath(request.OliveRecipeConfigPath!);
        if (!File.Exists(recipeConfigPath))
        {
            throw new FileNotFoundException($"Olive recipe config was not found: '{recipeConfigPath}'.", recipeConfigPath);
        }

        string variantAlias = string.IsNullOrWhiteSpace(request.VariantAlias)
            ? BuildVariantAlias(request.ExecutionProvider, request.Precision)
            : request.VariantAlias;
        string entryRelativePath = string.IsNullOrWhiteSpace(request.EntryRelativePath)
            ? request.ComponentRelativePaths[0]
            : request.EntryRelativePath;

        string tempOutputPath = request.OutputVariantPath + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(tempOutputPath);

        string oliveExe = _oliveEnvironment.GetOliveExecutablePath(request.ExecutionProvider);
        string cacheRoot = GetOliveCacheRoot(request.ModelId);
        string oliveOutputDir = CreateUniqueOliveWorkDirectory(cacheRoot, "recipe");
        Directory.CreateDirectory(oliveOutputDir);

        try
        {
            yield return $"Running Olive recipe: {Path.GetFileName(recipeConfigPath)}";

            await foreach (string line in RunOliveProcessAsync(
                oliveExe,
                ["run", "--config", recipeConfigPath],
                oliveOutputDir,
                cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }

            bool isGenAiBuilder = string.Equals(request.OliveMode, "ort-genai-builder", StringComparison.OrdinalIgnoreCase);
            if (isGenAiBuilder)
            {
                await foreach (string line in MaterializeGenAiRecipeOutputAsync(
                    request,
                    oliveOutputDir,
                    tempOutputPath,
                    variantAlias,
                    entryRelativePath,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return line;
                }
            }
            else
            {
                await foreach (string line in MaterializeOnnxRecipeOutputAsync(
                    request,
                    oliveOutputDir,
                    tempOutputPath,
                    variantAlias,
                    entryRelativePath,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return line;
                }
            }

            yield return "Optimization complete.";
        }
        finally
        {
            CleanupOliveWorkDirectories([oliveOutputDir], tempOutputPath);
        }
    }

    private async IAsyncEnumerable<string> MaterializeGenAiRecipeOutputAsync(
        ModelOptimizationRequest request,
        string oliveOutputDir,
        string tempOutputPath,
        string variantAlias,
        string entryRelativePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const string GenAiConfigFileName = "genai_config.json";
        string outputModelDir = ResolveGenAiRecipeOutputDirectory(oliveOutputDir, GenAiConfigFileName);

        foreach (string sourceFile in Directory.EnumerateFiles(outputModelDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(outputModelDir, sourceFile);
            string destFile = Path.Combine(tempOutputPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(sourceFile, destFile, overwrite: true);
        }

        string companionRoot = ResolveGenAiModelInputPath(
            request.ModelRootPath, request.EntryRelativePath, GenAiConfigFileName);
        CopyGenAiCompanionFiles(companionRoot, tempOutputPath, optimizedOnnx: null);

        var registration = new ModelOptimizedVariantRegistration(
            ModelId: request.ModelId,
            BaseModelRootPath: request.ModelRootPath,
            VariantAlias: variantAlias,
            VariantRootPath: request.OutputVariantPath,
            EntryRelativePath: GenAiConfigFileName,
            ComponentRelativePaths: [GenAiConfigFileName],
            OptimizerId: "olive",
            ExecutionProvider: ToExecutionProviderKind(request.ExecutionProvider),
            Precision: request.Precision,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Provenance: BuildProvenance(request));

        await CommitOutputDirectoryAndRegisterAsync(
            tempOutputPath,
            request.OutputVariantPath,
            registration,
            cancellationToken).ConfigureAwait(false);

        yield return $"Registered optimized variant: {variantAlias}";
    }

    private async IAsyncEnumerable<string> MaterializeOnnxRecipeOutputAsync(
        ModelOptimizationRequest request,
        string oliveOutputDir,
        string tempOutputPath,
        string variantAlias,
        string entryRelativePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<string> components = request.ComponentRelativePaths.Count > 0
            ? request.ComponentRelativePaths
            : [entryRelativePath];

        foreach (string relativePath in components)
        {
            string? optimizedPath = FindOptimizedOnnxPath(oliveOutputDir, relativePath);
            if (optimizedPath is null)
            {
                throw new InvalidOperationException(
                    $"Olive recipe did not produce an optimized ONNX artifact for '{relativePath}'.");
            }

            string targetPath = ResolveOutputComponentPath(tempOutputPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(optimizedPath, targetPath, overwrite: true);

            string sourceDataPath = optimizedPath + ".data";
            if (File.Exists(sourceDataPath))
            {
                File.Copy(sourceDataPath, targetPath + ".data", overwrite: true);
            }

            yield return $"  ready: {relativePath}";
        }

        var registration = new ModelOptimizedVariantRegistration(
            ModelId: request.ModelId,
            BaseModelRootPath: request.ModelRootPath,
            VariantAlias: variantAlias,
            VariantRootPath: request.OutputVariantPath,
            EntryRelativePath: entryRelativePath,
            ComponentRelativePaths: components,
            OptimizerId: "olive",
            ExecutionProvider: ToExecutionProviderKind(request.ExecutionProvider),
            Precision: request.Precision,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Provenance: BuildProvenance(request));

        await CommitOutputDirectoryAndRegisterAsync(
            tempOutputPath,
            request.OutputVariantPath,
            registration,
            cancellationToken).ConfigureAwait(false);

        yield return $"Registered optimized variant: {variantAlias}";
    }

    private static string ResolveGenAiRecipeOutputDirectory(string oliveOutputDir, string genAiConfigFileName)
    {
        if (File.Exists(Path.Combine(oliveOutputDir, genAiConfigFileName)))
        {
            return oliveOutputDir;
        }

        string? nested = Directory
            .EnumerateDirectories(oliveOutputDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, genAiConfigFileName)));

        if (nested is null)
        {
            throw new InvalidOperationException(
                $"Olive recipe did not produce '{genAiConfigFileName}' under '{oliveOutputDir}'.");
        }

        return nested;
    }

    private static string? FindOptimizedOnnxPath(string oliveOutputDir, string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        string? direct = Directory
            .EnumerateFiles(oliveOutputDir, fileName, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (direct is not null)
        {
            return direct;
        }

        string modelOnnx = Path.Combine(oliveOutputDir, "model.onnx");
        return File.Exists(modelOnnx) ? modelOnnx : null;
    }

    private async IAsyncEnumerable<string> RunOliveProcessAsync(
        string oliveExe,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (string line in _runner.RunAsync(
            oliveExe,
            arguments,
            workingDirectory,
            cancellationToken).ConfigureAwait(false))
        {
            if (OliveOptimizationProgress.TryFormatProgressLine(line, out string progressLine))
            {
                yield return progressLine;
            }

            yield return line;
        }
    }
}

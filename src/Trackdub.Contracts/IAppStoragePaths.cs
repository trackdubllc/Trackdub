namespace Trackdub.Contracts;

public interface IAppStoragePaths
{
    string RootDirectory { get; }

    string UserDataRoot { get; }

    string UserCacheRoot { get; }

    string? SharedAssetRoot { get; }

    bool IsPortable { get; }

    string ModelCacheDirectory { get; }

    string ModelCacheIndexPath { get; }

    string LogFilePath { get; }

    string SettingsPath { get; }

    string LayoutPath { get; }

    string ToolCacheDirectory { get; }

    string FfmpegToolCacheDirectory { get; }

    string EngineCacheDirectory { get; }

    string ComponentCacheDirectory { get; }
}

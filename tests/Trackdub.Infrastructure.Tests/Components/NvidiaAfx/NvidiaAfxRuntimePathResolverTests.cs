using Trackdub.Contracts;
using Trackdub.Infrastructure.Components;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Infrastructure.Tests.Components.NvidiaAfx;

public sealed class NvidiaAfxRuntimePathResolverTests
{
    [Fact]
    public void ResolveRuntimeRoot_PrefersConfiguredDirectory()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-path-{Guid.NewGuid():N}");
        string configured = Path.Join(tempRoot, "configured");
        Directory.CreateDirectory(configured);
        try
        {
            var store = new ComponentStore(tempRoot, new NoopLogger());
            string? resolved = NvidiaAfxRuntimePathResolver.ResolveRuntimeRoot(store, configured);
            Assert.Equal(Path.GetFullPath(configured), resolved);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void HasNativeLibrary_False_WhenDllMissing()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            Assert.False(NvidiaAfxRuntimePathResolver.HasNativeLibrary(tempRoot));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class NoopLogger : IApplicationLogger
    {
        public void LogDebug(string message) { }
        public void LogInformation(string message) { }
        public void LogWarning(string message, Exception? exception = null) { }
        public void LogError(string message, Exception? exception = null) { }
    }
}

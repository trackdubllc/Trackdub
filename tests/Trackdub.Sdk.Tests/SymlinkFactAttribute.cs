namespace Trackdub.Sdk.Tests;

/// <summary>
/// Custom [Fact] attribute that skips the test at discovery time when the host cannot create
/// symbolic links. Creating a link needs <c>SeCreateSymbolicLinkPrivilege</c> or Developer Mode on
/// Windows and is refused on some container and CI file systems. Probing in the constructor makes
/// such a test report as skipped with a reason, rather than passing without asserting anything.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (CanCreateSymbolicLinks())
        {
            return;
        }

        Skip = "This host cannot create symbolic links " +
               "(Windows needs Developer Mode or SeCreateSymbolicLinkPrivilege; some CI file systems refuse).";
    }

    private static bool CanCreateSymbolicLinks()
    {
        string probeTarget = Path.Join(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        string probeLink = probeTarget + "-link";

        try
        {
            Directory.CreateDirectory(probeTarget);
            Directory.CreateSymbolicLink(probeLink, probeTarget);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(probeLink))
                {
                    Directory.Delete(probeLink);
                }

                if (Directory.Exists(probeTarget))
                {
                    Directory.Delete(probeTarget);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                /* best-effort cleanup */
            }
        }
    }
}
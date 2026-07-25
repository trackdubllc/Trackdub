using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Trackdub.Licensing;

/// <summary>
/// Dispatches to the appropriate platform-specific fingerprint source via RuntimeInformation,
/// then hashes the raw machine identifier with SHA-256 to produce a 64-char lowercase hex string.
/// All platform exceptions are caught and re-thrown as <see cref="FingerprintException"/>.
/// </summary>
public sealed class HardwareFingerprintProvider : IHardwareFingerprintProvider
{
    public string GetFingerprint()
    {
        try
        {
            var source = CreatePlatformSource();
            var rawId = source.GetRawMachineId().Trim();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawId));
            return Convert.ToHexStringLower(hash);
        }
        catch (FingerprintException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FingerprintException(
                "Failed to generate hardware fingerprint.", ex);
        }
    }

    private static IFingerprintSource CreatePlatformSource()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsFingerprintSource();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsFingerprintSource();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxFingerprintSource();
        }

        throw new PlatformNotSupportedException(
            $"Hardware fingerprint generation is not supported on this platform: {RuntimeInformation.OSDescription}");
    }
}

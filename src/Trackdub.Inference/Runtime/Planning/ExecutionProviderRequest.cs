using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

public static class ExecutionProviderRequest
{
    public static ExecutionProviderKind? ParsePreferredExecutionProvider(
        string? preferredExecutionProvider,
        bool requirePreferredExecutionProvider)
    {
        if (string.IsNullOrWhiteSpace(preferredExecutionProvider))
        {
            return null;
        }

        string trimmed = preferredExecutionProvider.Trim();

        // Try canonical token table first (covers trt-rtx, dnnl, coreml, etc.)
        if (ExecutionProviderTokens.TryParse(trimmed, out ExecutionProviderKind tokenKind))
        {
            return tokenKind;
        }

        // Fall back to enum name for backward compatibility
        if (Enum.TryParse(trimmed, ignoreCase: true, out ExecutionProviderKind provider))
        {
            return provider;
        }

        if (requirePreferredExecutionProvider)
        {
            throw new InvalidOperationException(
                $"Execution provider override '{preferredExecutionProvider}' is not recognized.");
        }

        return null;
    }
}

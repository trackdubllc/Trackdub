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

        if (Enum.TryParse(preferredExecutionProvider.Trim(), ignoreCase: true, out ExecutionProviderKind provider))
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

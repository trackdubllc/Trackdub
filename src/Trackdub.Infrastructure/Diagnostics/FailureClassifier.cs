using Trackdub.Contracts.Diagnostics;
using SharedFailureClassifier = Trackdub.Contracts.Diagnostics.DiagnosticFailureClassifier;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Maps known exception types to the appropriate <see cref="FailureCategory"/>.
/// </summary>
public static class FailureClassifier
{
    /// <summary>
    /// Classifies an exception into a <see cref="FailureCategory"/> based on its type and message.
    /// </summary>
    public static FailureCategory Classify(Exception exception) =>
        SharedFailureClassifier.Classify(exception);
}

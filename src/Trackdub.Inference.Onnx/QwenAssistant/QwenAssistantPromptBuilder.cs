using Trackdub.Contracts;

namespace Trackdub.Inference.Onnx.QwenAssistant;

public static class QwenAssistantPromptBuilder
{
    private const string OnboardingSystemPrompt =
        """
        You are the Trackdub setup assistant. Trackdub is a local-first AI video
        dubbing tool. It transcribes, translates, synthesizes voices, and syncs
        audio/lips to video, all on the user's device. Answer questions about
        getting started, what starter packs are, and how to pick one. If asked
        about anything unrelated to Trackdub, politely redirect. Be concise.
        """;

    private const string StarterPackExplainSystemPrompt =
        """
        You explain Trackdub starter packs. A starter pack is a bundle of AI
        models for a specific hardware tier (Basic = CPU, Balanced = mid GPU,
        Premium = high-end GPU, Cloud = API). Given a pack definition in JSON,
        explain what it does and why it was recommended. Be friendly and brief.
        """;

    private const string StarterPackAuditSystemPrompt =
        """
        You audit a Trackdub starter pack compatibility report. If issues exist,
        output a JSON array of patch operations. Each operation has: "kind" (one
        of: SetStageExecutionProvider, SwapStageModelAlias, SetOptionalModelEnabled,
        FlagNotRunnable), "stage" (pipeline stage name or null), "value" (new
        value), "reason" (plain English why). If the pack is fine, output an
        empty array. Output JSON only, no prose.
        """;

    private const string GeneralHelpSystemPrompt =
        """
        You are a helpful Trackdub assistant. Answer questions about using
        Trackdub. If you don't know the answer, say so, do not guess. Be concise.
        """;

    public static string BuildPrompt(LocalAssistantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string systemPrompt = request.Scope switch
        {
            LocalAssistantScope.Onboarding => OnboardingSystemPrompt,
            LocalAssistantScope.StarterPackExplain => StarterPackExplainSystemPrompt,
            LocalAssistantScope.StarterPackAudit => StarterPackAuditSystemPrompt,
            LocalAssistantScope.GeneralHelp => GeneralHelpSystemPrompt,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Scope, "Unsupported assistant scope.")
        };

        string userContent = string.IsNullOrWhiteSpace(request.ContextJson)
            ? request.UserMessage
            : $"Context:\n{request.ContextJson}\n\nQuestion: {request.UserMessage}";

        return
            $"<|im_start|>system\n{systemPrompt}\n" +
            $"<|im_start|>user\n{userContent}\n" +
            "<|im_start|>assistant\n";
    }
}

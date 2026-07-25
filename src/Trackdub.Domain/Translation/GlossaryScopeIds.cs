namespace Trackdub.Domain.Translation;

public static class GlossaryScopeIds
{
    /// <summary>Sentinel project id for user-level global glossary entries.</summary>
    public static readonly Guid Global = new("00000000-0000-4000-8000-000000000001");

    public static bool IsGlobalScope(Guid projectId) => projectId == Global;
}

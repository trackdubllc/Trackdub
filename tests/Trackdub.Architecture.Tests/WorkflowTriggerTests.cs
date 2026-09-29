namespace Trackdub.Architecture.Tests;

/// <summary>
/// Guards the pull-request triggers that keep stacked pull requests (base = another branch) in CI.
/// </summary>
/// <remarks>
/// A <c>branches:</c> filter on <c>pull_request</c> silently drops every stacked pull request from
/// the run: the workflow fires only for pull requests whose base is <c>main</c>, so a stacked branch
/// reaches <c>main</c> without ever having run those checks. Asserting the filter out here stops a
/// later edit from reintroducing it unnoticed.
/// </remarks>
public sealed class WorkflowTriggerTests
{
    [Fact]
    public void CiWorkflow_pull_request_trigger_is_not_narrowed_to_a_base_branch()
    {
        string workflow = File.ReadAllText(ResolveRepositoryFile(".github/workflows/ci.yml"));

        IReadOnlyList<string> filters = ReadTriggerFilters(workflow, "pull_request");

        Assert.True(
            filters.Count == 0,
            ".github/workflows/ci.yml narrows its pull_request trigger with "
            + $"{string.Join(", ", filters)}. A stacked pull request (base = another branch) then "
            + "never runs the matrix, the format job, the repository-boundary job or the "
            + "controlled-budget job. Remove the base-branch filter, passing every pull request as "
            + "the trigger's comment describes; if CI is deliberately main-only again, delete this "
            + "test in the same commit.");
    }

    [Fact]
    public void Base_branch_filter_is_detected_on_the_trigger_that_carries_it()
    {
        // An empty result is what the guard above wants to see, and it is also what a reader that
        // cannot see anything would return, so pin both directions and the sibling trigger
        // boundaries on samples rather than trusting the real file alone.
        Assert.Contains("branches", ReadTriggerFilters(NarrowedSample, "pull_request"));
        Assert.Empty(ReadTriggerFilters(UnfilteredSample, "pull_request"));
        Assert.Contains("branches", ReadTriggerFilters(UnfilteredSample, "push"));
    }

    private const string NarrowedSample = """
        name: CI

        on:
          push:
            branches: [main]
          pull_request:
            branches: [main]
          workflow_dispatch:
        """;

    private const string UnfilteredSample = """
        name: CI

        on:
          push:
            branches: [main]
          # A comment at trigger indentation, between the two triggers.
          pull_request:
          workflow_dispatch:
        """;

    /// <summary>
    /// Returns the filter keys declared directly under <paramref name="trigger"/> in the workflow's
    /// top-level <c>on:</c> block — <c>branches</c>, <c>paths</c>, <c>types</c> and friends. An
    /// empty result means the trigger runs for every value along that axis.
    /// </summary>
    private static IReadOnlyList<string> ReadTriggerFilters(string workflow, string trigger)
    {
        string[] lines = workflow.ReplaceLineEndings("\n").Split('\n');

        int onIndex = Array.FindIndex(
            lines,
            static line => line.Length > 0 && line[0] != ' ' && line.TrimEnd() == "on:");
        Assert.True(onIndex >= 0, "Workflow declares no top-level 'on:' block.");

        int triggerIndex = -1;
        int triggerIndent = int.MaxValue;
        for (int i = onIndex + 1; i < lines.Length; i++)
        {
            if (IsBlankOrComment(lines[i]))
            {
                continue;
            }

            int indent = IndentOf(lines[i]);
            if (indent == 0)
            {
                break; // the 'on:' block ends at the next top-level key
            }

            if (indent > triggerIndent)
            {
                continue; // a filter of an already seen sibling trigger
            }

            triggerIndent = indent;
            if (ContentOf(lines[i]) == trigger + ":")
            {
                triggerIndex = i;
                break;
            }
        }

        Assert.True(
            triggerIndex >= 0,
            $"Workflow declares no '{trigger}' trigger under its top-level 'on:' block.");

        var filters = new List<string>();
        int? filterIndent = null;
        for (int i = triggerIndex + 1; i < lines.Length; i++)
        {
            if (IsBlankOrComment(lines[i]))
            {
                continue;
            }

            int indent = IndentOf(lines[i]);
            if (indent <= triggerIndent)
            {
                break; // the next sibling trigger, or the end of the 'on:' block
            }

            filterIndent ??= indent;
            if (indent != filterIndent)
            {
                continue; // a value nested under a filter, not a filter key
            }

            string content = ContentOf(lines[i]);
            int colon = content.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                filters.Add(content[..colon].Trim());
            }
        }

        return filters;
    }

    private static string ResolveRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Join(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from '{AppContext.BaseDirectory}'.");
    }

    private static bool IsBlankOrComment(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    /// <summary>Trims the line and drops any inline comment, so key comparisons ignore both.</summary>
    private static string ContentOf(string line)
    {
        string trimmed = line.TrimEnd();
        int comment = trimmed.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? trimmed[..comment] : trimmed).Trim();
    }
}

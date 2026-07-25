namespace Trackdub.Cli;

/// <summary>
/// Generates shell completion scripts that call <c>trackdub complete</c> (no dotnet-suggest dependency).
/// </summary>
internal static class CliCompletionScripts
{
    internal static string Generate(string shell, string executableName)
    {
        return shell.ToLowerInvariant() switch
        {
            "bash" => GenerateBash(executableName),
            "zsh" => GenerateZsh(executableName),
            "pwsh" or "powershell" => GeneratePowerShell(executableName),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Supported shells: bash, zsh, pwsh"),
        };
    }

    private static string GenerateBash(string executableName) =>
        $$"""
        # Trackdub shell completion for bash
        # Usage: eval "$(trackdub completion bash)"
        # Or: source <(trackdub completion bash)

        _trackdub_bash_complete()
        {
          local executable="${COMP_WORDS[0]}"
          local completions
          completions=$("$executable" complete --position "${COMP_POINT}" --line "${COMP_LINE}") || return
          local IFS=$'\n'
          local suggestions=($(compgen -W "$completions" -- "${COMP_WORDS[COMP_CWORD]}"))
          COMPREPLY=("${suggestions[@]}")
        }

        complete -F _trackdub_bash_complete {{executableName}}
        """;

    private static string GenerateZsh(string executableName) =>
        $$"""
        # Trackdub shell completion for zsh
        # Usage: eval "$(trackdub completion zsh)"

        _trackdub_zsh_complete()
        {
          local executable="${words[1]}"
          local full_line="$words"
          local completions
          completions=("${(@f)$("$executable" complete --position "$CURSOR" --line "$full_line")}") || return 1
          compadd -a completions
        }

        compdef _trackdub_zsh_complete {{executableName}}
        """;

    private static string GeneratePowerShell(string executableName) =>
        $$"""
        # Trackdub shell completion for PowerShell
        # Usage: trackdub completion pwsh | Out-String | Invoke-Expression

        $TrackdubExecutableName = '{{executableName}}'

        Register-ArgumentCompleter -Native -CommandName $TrackdubExecutableName -ScriptBlock {
          param($commandName, $wordToComplete, $commandAst, $cursorPosition)

          $executable = $commandAst.CommandElements[0].Value
          $line = $commandAst.ToString()
          $completions = & $executable complete --position $cursorPosition --line $line
          if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($completions)) {
            return @()
          }

          $completions.Split([Environment]::NewLine, [System.StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object {
              [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;
}

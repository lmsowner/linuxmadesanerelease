// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Services;

public static class RunbookScaffold
{
    public static string Describe(RunbookTargetKind kind) => kind switch
    {
        RunbookTargetKind.File => "One selected file arrives as $1. Validate it, then process it once.",
        RunbookTargetKind.Files => "Selected files arrive as quoted Bash arguments ($@). Validate every file before processing any.",
        RunbookTargetKind.Folder => "One selected folder arrives as $1. Work inside that folder; recursion is explicit.",
        RunbookTargetKind.Folders => "Selected folders arrive as quoted Bash arguments ($@). Validate every folder before processing any.",
        RunbookTargetKind.FilesAndFolders => "Selected files and folders arrive as quoted Bash arguments ($@). Check each path’s type before processing it; recursion is explicit.",
        _ => "Runs on each selected machine. Check that machine's prerequisites before making changes."
    };

    public static string BuildInputValidation(RunbookTargetKind kind, string failureFunction = "fail")
    {
        var validation = kind switch
        {
            RunbookTargetKind.File => "[[ $# -eq 1 && -f \"$1\" && -r \"$1\" ]] || fail 'Select one readable file.'",
            RunbookTargetKind.Files => "[[ $# -gt 0 ]] || fail 'Select at least one file.'\nfor input in \"$@\"; do\n  [[ -f \"$input\" && -r \"$input\" ]] || fail \"Not a readable file: $input\"\ndone",
            RunbookTargetKind.Folder => "[[ $# -eq 1 && -d \"$1\" && -r \"$1\" && -x \"$1\" ]] || fail 'Select one accessible folder.'",
            RunbookTargetKind.Folders => "[[ $# -gt 0 ]] || fail 'Select at least one folder.'\nfor input in \"$@\"; do\n  [[ -d \"$input\" && -r \"$input\" && -x \"$input\" ]] || fail \"Not an accessible folder: $input\"\ndone",
            RunbookTargetKind.FilesAndFolders => "[[ $# -gt 0 ]] || fail 'Select at least one file or folder.'\nfor input in \"$@\"; do\n  [[ -f \"$input\" && -r \"$input\" ]] || [[ -d \"$input\" && -r \"$input\" && -x \"$input\" ]] || fail \"Not a readable file or accessible folder: $input\"\ndone",
            _ => "[[ $# -eq 0 ]] || fail 'This machine runbook takes no file arguments.'"
        };
        return validation.Replace("fail ", failureFunction + " ", StringComparison.Ordinal);
    }

    public static string BuildInputPrompt(RunbookTargetKind kind)
    {
        if (kind == RunbookTargetKind.Machine) return string.Empty;
        var single = kind is RunbookTargetKind.File or RunbookTargetKind.Folder;
        return $$"""
# Direct terminal use: ask for paths only when stdin is an interactive terminal.
# File-browser and scheduled runs already supply arguments and never prompt.
if [[ $# -eq 0 && -t 0 ]]; then
  printf '{{RunbookSelectionSupport.Label(kind)}}: enter absolute paths, one per line{{(single ? "." : "; an empty line finishes.")}}\n' >&2
  lms_input_paths=()
  while IFS= read -r -p 'Path: ' lms_input_path; do
    [[ -n "$lms_input_path" ]] || break
    lms_input_paths+=("$lms_input_path")
    {{(single ? "break" : ":")}}
  done
  set -- "${lms_input_paths[@]}"
fi
""";
    }

    public static string Build(RunbookTargetKind kind)
    {
        var validation = BuildInputValidation(kind);
        var process = kind == RunbookTargetKind.Machine ? "process" : "for input in \"$@\"; do process \"$input\"; done";
        return $$"""
#!/usr/bin/env bash
set -euo pipefail

fail() { printf 'Runbook: %s\n' "$*" >&2; exit 1; }
trap 'printf "Runbook failed at line %s\n" "$LINENO" >&2' ERR

# List the actual executable names needed by this runbook, e.g. (curl jq).
required_commands=()
check_prerequisites() {
  local tool
  local missing=()
  for tool in ${required_commands[@]+"${required_commands[@]}"}; do
    command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
  done
  if (( ${#missing[@]} > 0 )); then
    printf 'Missing commands on %s: %s\n' "${HOSTNAME:-this machine}" "${missing[*]}" >&2
    fail 'Install the packages providing these commands with this machine’s package manager, then retry.'
  fi
}

# Validate all inputs before doing any work. Paths may contain spaces or quotes.
{{BuildInputPrompt(kind)}}
{{validation}}
check_prerequisites

prepare() {
  # Check configuration, permissions and destination conflicts here.
  :
}
process() {
  # Implement the tool here. File/folder input is "$1"; preserve quoted paths.
  # Do not overwrite originals or recurse unless explicitly intended.
  :
}
finish() {
  # Report outputs and clean up temporary resources here.
  printf 'Runbook completed successfully.\n'
}

prepare
{{process}}
finish
""";
    }
}

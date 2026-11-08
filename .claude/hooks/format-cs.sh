#!/usr/bin/env bash
# PostToolUse: format the C# file that was just edited (whitespace only, that file only).
file=$(jq -r '.tool_input.file_path // empty')
[[ "$file" == *.cs && -f "$file" ]] || exit 0
[[ "$file" == */obj/* || "$file" == */bin/* || "$file" == *.g.cs ]] && exit 0
cd "${CLAUDE_PROJECT_DIR:-.}" && dotnet format whitespace FitzLauncher.slnx --include "$file" >/dev/null 2>&1 || true
exit 0

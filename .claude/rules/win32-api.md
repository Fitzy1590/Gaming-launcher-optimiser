---
paths: ["**/Native.cs", "src/FitzLauncher.Windows/**/*.cs"]
---

# Win32 / ntdll rules

- Declare P/Invoke next to the feature that uses it; use `[LibraryImport]`. Do not change existing signatures, struct layouts or constants unless the task needs it.
- Note the source in a comment (Microsoft docs URL, or "ntdll community headers: VERIFY on device" for undocumented calls such as `NtQueryTimerResolution`, `NtSetInformationProcess`).
- Open handles with the least access (`PROCESS_SUSPEND_RESUME`, `PROCESS_SET_INFORMATION`, `PROCESS_QUERY_LIMITED_INFORMATION`) and always close them.
- Catch specific types (`Win32Exception`, `InvalidOperationException`, `DllNotFoundException`, `EntryPointNotFoundException`) with `when` filters. Background threads never throw out.
- Run external tools with `UseShellExecute = false`, `CreateNoWindow = true` and a `WaitForExit` timeout.
- Never inject into game processes; skip anti-cheat games for DLL swaps.

---
paths: ["**/FitzLauncher.Windows/**/*.cs"]
---

# Win32 and ntdll rules

- New P/Invoke goes next to the feature that uses it, with `[LibraryImport]` and a comment naming the source. Undocumented ntdll calls (`NtQueryTimerResolution`, `NtSetInformationProcess` classes) are marked VERIFY on device.
- Never change existing signatures, struct layouts or constants unless the task needs it.
- External tools run with `UseShellExecute = false`, `CreateNoWindow = true` and a `WaitForExit` timeout.
- Every change to a game, the OS or the hardware writes a restore record first and has a start-up recovery.

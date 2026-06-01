Check if the current phase acceptance criteria are fully met:

1. Run `dotnet build ReconPlatform.sln` — must pass with zero warnings
2. Run `dotnet test tests/ReconPlatform.UnitTests` — must pass
3. Check each acceptance criterion listed in TASKS.md for the current phase and report pass / fail per item
4. If all pass:
   a. Mark all tasks in the current phase as `[x]` in TASKS.md
   b. Update `.claude/context.md`: set Last Completed Task and Next Task
   c. Report: "Phase N complete. Next: Phase N+1 — <goal>"
5. If any criterion fails, list what remains and do NOT mark the phase done.

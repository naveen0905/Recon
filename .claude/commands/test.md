Run `dotnet test tests/ReconPlatform.UnitTests --configuration Release --logger "console;verbosity=normal"` and report:
1. Total tests: passed / failed / skipped
2. Full details of any failures: test name, expected vs actual, stack trace snippet
3. Which Phase 1 acceptance-criteria tests are passing vs missing

Also run `pytest tests/python -v` if the `tests/python/` directory exists. Report failures the same way.

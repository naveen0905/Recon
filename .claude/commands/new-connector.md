Scaffold a new connector. Usage: /new-connector <Name> <type>
Example: /new-connector GitHubIssues rest_api

Steps to perform:
1. Create `src/ReconPlatform.Connectors/<Name>Connector.cs` implementing `IConnector`
   - Use `RestApiConnector`, `AzureSqlConnector`, or `AzureAdxConnector` as a template
   - All async methods must accept `CancellationToken`
   - Use Polly retry policy from base class
2. Create `tests/ReconPlatform.UnitTests/Connectors/<Name>ConnectorTests.cs`
   - Mock all Azure SDK and HTTP calls
   - Test: successful pull returns normalized list, connection failure throws, empty source returns empty list
3. Add example YAML source config block to `docs/configuration.md`
4. If type is `plugin`, also create `plugins/<Name>Plugin.cs` as the drop-in example

Follow all rules in `CLAUDE.md`. Do not hardcode any credentials.

# Plugins

Drop custom connector implementations here. Each plugin must implement the
`IConnector` interface (`src/ReconPlatform.Connectors/Interfaces/IConnector.cs`).

## Registering a plugin

In your team YAML config:

```yaml
sources:
  - id: my-custom-source
    type: plugin
    plugin_class: MyCompany.Plugins.MyConnector   # fully-qualified type name
    config:
      endpoint: https://internal.corp.com/api
```

The `PluginLoader` resolves the type from the loaded assemblies. Place your
compiled assembly in the `plugins/` directory or reference it in the project.

## Security

Plugin class paths are restricted to assemblies in this directory.
Path traversal attempts are rejected by `PluginLoader`.

See `docs/extending-skills.md` for a step-by-step walkthrough.

using Microsoft.Extensions.Logging;
using ReconPlatform.Connectors.Interfaces;

namespace ReconPlatform.Connectors;

public static class PluginLoader
{
    public static IConnector Load(string pluginClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginClass);

        if (!pluginClass.StartsWith("plugins.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Plugin class '{pluginClass}' is not permitted. Class names must start with 'plugins.'.");

        var simpleClassName = pluginClass["plugins.".Length..];

        var type = FindType(pluginClass, simpleClassName);

        if (!typeof(IConnector).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"Type '{type.FullName}' does not implement IConnector.");

        return Activate(type, pluginClass);
    }

    private static Type FindType(string fullPluginClass, string simpleClassName)
    {
        var searchAssemblies = new[]
        {
            System.Reflection.Assembly.GetEntryAssembly(),
            System.Reflection.Assembly.GetExecutingAssembly(),
        }
        .Concat(AppDomain.CurrentDomain.GetAssemblies())
        .OfType<System.Reflection.Assembly>()
        .Distinct();

        foreach (var assembly in searchAssemblies)
        {
            var type = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IConnector).IsAssignableFrom(t) &&
                !t.IsAbstract &&
                !t.IsInterface &&
                (string.Equals(t.FullName, fullPluginClass, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.Name, simpleClassName, StringComparison.OrdinalIgnoreCase)));

            if (type is not null)
                return type;
        }

        throw new InvalidOperationException(
            $"No IConnector implementation found for plugin class '{fullPluginClass}'. " +
            "Ensure the plugin assembly is loaded and the class name is correct.");
    }

    private static IConnector Activate(Type type, string pluginClass)
    {
        // Prefer parameterless constructor; fall back to single ILogger parameter.
        var parameterlessCtor = type.GetConstructor(Type.EmptyTypes);
        if (parameterlessCtor is not null)
        {
            return (IConnector)Activator.CreateInstance(type)!;
        }

        var loggerType = typeof(ILogger<>).MakeGenericType(type);
        var loggerCtor = type.GetConstructor([loggerType]);
        if (loggerCtor is not null)
        {
            var nullLogger = Activator.CreateInstance(
                typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(type))!;
            return (IConnector)Activator.CreateInstance(type, nullLogger)!;
        }

        throw new InvalidOperationException(
            $"Plugin type '{pluginClass}' has no parameterless constructor or single-ILogger constructor. " +
            "Plugin connectors must expose one of these constructors.");
    }
}

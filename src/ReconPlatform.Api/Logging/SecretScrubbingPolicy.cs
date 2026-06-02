using Serilog.Core;
using Serilog.Events;

namespace ReconPlatform.Api.Logging;

/// <summary>
/// Serilog destructuring policy that redacts string properties whose name
/// matches any of the secret-like patterns defined by SOC2 requirements.
///
/// Patterns (case-insensitive, substring match):
///   *secret*, *password*, *key*, *token*, *connection*, *conn*
///
/// Redacted properties are replaced with the literal string "[redacted]".
/// This policy is applied globally via <c>.Destructure.With&lt;SecretScrubbingPolicy&gt;()</c>.
/// </summary>
public sealed class SecretScrubbingPolicy : IDestructuringPolicy
{
    private static readonly string[] ScrubPatterns =
    [
        "secret", "password", "key", "token", "connection", "conn",
    ];

    /// <inheritdoc />
#pragma warning disable CS8767 // Nullability of out parameter matches the interface contract at runtime
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        // This policy only handles anonymous objects/structured values.
        // Serilog calls this when destructuring an object with @ operator.
        if (value is null)
        {
            result = new ScalarValue(null);
            return false;
        }

        var valueType = value.GetType();

        // Only intercept complex objects (not primitives/strings at the top level).
        if (valueType.IsPrimitive || value is string || value is decimal || value is DateTimeOffset || value is DateTime)
        {
            result = new ScalarValue(null);
            return false;
        }

        var properties = new List<LogEventProperty>();
        var scrubbed   = false;

        foreach (var prop in valueType.GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                continue;

            object? propValue;
            try { propValue = prop.GetValue(value); }
            catch { propValue = null; }

            LogEventPropertyValue logValue;

            if (propValue is string strValue && IsSensitive(prop.Name))
            {
                logValue = new ScalarValue("[redacted]");
                scrubbed = true;
            }
            else
            {
                logValue = propertyValueFactory.CreatePropertyValue(propValue, destructureObjects: true);
            }

            properties.Add(new LogEventProperty(prop.Name, logValue));
        }

        if (!scrubbed)
        {
            result = new ScalarValue(null);
            return false;
        }

        result = new StructureValue(properties);
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> if the property name contains any of the scrub patterns
    /// (case-insensitive substring match).
    /// </summary>
    public static bool IsSensitive(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return false;

        foreach (var pattern in ScrubPatterns)
        {
            if (propertyName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

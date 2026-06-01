using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace ReconPlatform.Config;

internal sealed class YamlEnumConverter : IYamlTypeConverter
{
    public static readonly YamlEnumConverter Instance = new();

    public bool Accepts(Type type) => type.IsEnum;

    public object ReadYaml(IParser parser, Type type)
    {
        var scalar = parser.Consume<Scalar>();
        var yamlValue = scalar.Value;

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var alias = field.GetCustomAttribute<YamlMemberAttribute>()?.Alias;
            if (alias is not null && alias == yamlValue)
                return Enum.Parse(type, field.Name);
        }

        return Enum.Parse(type, yamlValue, ignoreCase: true);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type)
    {
        if (value is null) return;
        var fieldName = value.ToString()!;
        var field = type.GetField(fieldName);
        var alias = field?.GetCustomAttribute<YamlMemberAttribute>()?.Alias;
        emitter.Emit(new Scalar(alias ?? fieldName));
    }
}

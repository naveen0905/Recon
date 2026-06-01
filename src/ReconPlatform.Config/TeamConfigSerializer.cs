using ReconPlatform.Config.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReconPlatform.Config;

public static class TeamConfigSerializer
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(YamlEnumConverter.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static TeamConfig Deserialize(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        return Deserializer.Deserialize<TeamConfig>(yaml);
    }

    public static string Serialize(TeamConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(YamlEnumConverter.Instance)
            .Build();
        return serializer.Serialize(config);
    }
}

using FluentAssertions;
using ReconPlatform.Connectors;
using Xunit;

namespace ReconPlatform.UnitTests.Connectors;

public class PluginLoaderTests
{
    [Fact]
    public void Load_ClassNotStartingWithPlugins_ThrowsInvalidOperation()
    {
        var act = () => PluginLoader.Load("SomeOther.Assembly.Class");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*plugins.*");
    }

    [Fact]
    public void Load_NonExistentPluginClass_ThrowsInvalidOperation()
    {
        var act = () => PluginLoader.Load("plugins.NonExistentConnector99");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Load_EmptyPluginClass_ThrowsArgument()
    {
        var act = () => PluginLoader.Load("");
        act.Should().Throw<ArgumentException>();
    }
}

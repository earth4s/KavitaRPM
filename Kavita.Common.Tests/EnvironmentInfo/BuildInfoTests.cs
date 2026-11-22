using System.Reflection;
using Kavita.Common.EnvironmentInfo;

namespace Kavita.Common.Tests.EnvironmentInfo;

public class BuildInfoTests
{
    [Fact]
    public void DisplayVersion_IdentifiesForkWithoutChangingNumericVersion()
    {
        var numericVersion = typeof(BuildInfo).Assembly.GetName().Version!;
        Assert.Equal(numericVersion, BuildInfo.Version);
        Assert.StartsWith(numericVersion.ToString(4) + "-kavitarpm", BuildInfo.DisplayVersion);
        Assert.DoesNotContain("+", BuildInfo.DisplayVersion);
        Assert.Equal(numericVersion, Version.Parse(BuildInfo.DisplayVersion.Split('-')[0]));
    }

    [Fact]
    public void DisplayVersion_UsesStampedReleaseIdentity()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(informational.Split('+')[0], BuildInfo.DisplayVersion);
        var expected = Environment.GetEnvironmentVariable("EXPECTED_KAVITARPM_VERSION");
        if (expected != null)
        {
            Assert.Equal(expected, BuildInfo.DisplayVersion);
        }
    }
}

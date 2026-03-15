using Xunit;

namespace Atlas.ControlPlane.Tests.Integration;

public sealed class RequiresEnvironmentVariableFactAttribute : FactAttribute
{
    public RequiresEnvironmentVariableFactAttribute(string environmentVariableName)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariableName)))
        {
            Skip = $"Requires environment variable '{environmentVariableName}' to run.";
        }
    }
}


using EPDeskExtractionSandbox.Configuration;

namespace EPDeskExtractionSandbox.Tests;

public sealed class SandboxOptionsValidatorTests
{
    [Fact]
    public void Validate_AcceptsBoundedContainerDefaults()
    {
        var options = new SandboxOptions
        {
            SharedApiKey = new string('s', 32)
        };

        var result = new SandboxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validate_RejectsUnsafeSocketAndUnboundedInput()
    {
        var options = new SandboxOptions
        {
            SharedApiKey = new string('s', 32),
            ClamSocketPath = "/tmp/public.sock",
            MaxInputBytes = 536_870_913
        };

        var result = new SandboxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_RejectsInvalidMinimumClamVersion()
    {
        var options = new SandboxOptions
        {
            SharedApiKey = new string('s', 32),
            MinimumClamVersion = "latest"
        };

        var result = new SandboxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }
}

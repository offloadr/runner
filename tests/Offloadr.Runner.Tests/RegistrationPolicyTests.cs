
namespace Offloadr.Runner.Tests;

public class RegistrationPolicyTests
{
    [Test]
    public void ComputeBackoff_RespectsBounds()
    {
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(10);

        var delay = RegistrationPolicy.ComputeBackoff(attempt: 4, min, max, randomSample: 1.0);

        Assert.That(delay, Is.EqualTo(max));
    }

    [Test]
    public void ComputeBackoff_UsesMinWhenJitterTooSmall()
    {
        var min = TimeSpan.FromSeconds(2);
        var max = TimeSpan.FromSeconds(30);

        var delay = RegistrationPolicy.ComputeBackoff(attempt: 3, min, max, randomSample: 0.0);

        Assert.That(delay, Is.EqualTo(min));
    }

    [Test]
    public void TryExtractClientId_ReadsClientIdFromJson()
    {
        var clientId = RegistrationPolicy.TryExtractClientId("{\"client_id\":\"abc\"}");

        Assert.That(clientId, Is.EqualTo("abc"));
    }

    [Test]
    public void TryExtractClientId_ReturnsNullForMissingOrInvalidJson()
    {
        Assert.That(RegistrationPolicy.TryExtractClientId("{\"x\":1}"), Is.Null);
        Assert.That(RegistrationPolicy.TryExtractClientId("not json"), Is.Null);
    }

    [Test]
    public void TryExtractPromptId_ReadsPromptIdFromJson()
    {
        var promptId = RegistrationPolicy.TryExtractPromptId("{\"prompt_id\":\"p-1\"}");

        Assert.That(promptId, Is.EqualTo("p-1"));
    }

    [Test]
    public void TryExtractPromptId_ReturnsNullForMissingOrInvalidJson()
    {
        Assert.That(RegistrationPolicy.TryExtractPromptId("{\"x\":1}"), Is.Null);
        Assert.That(RegistrationPolicy.TryExtractPromptId("{"), Is.Null);
    }
}

namespace Offloadr.Runner.Tests;

public class GrpcChannelManagerTests
{
    [TestCase("https://api.example.test", "https://api.example.test")]
    [TestCase("https://user:secret@api.example.test:8443/base?token=secret", "https://api.example.test:8443")]
    [TestCase("http://control-plane:31080/", "http://control-plane:31080")]
    [TestCase("not a url", "(unparsable url)")]
    public void RedactForLog_KeepsOnlySchemeHostAndPort(string url, string expected)
    {
        Assert.That(GrpcChannelManager.RedactForLog(url), Is.EqualTo(expected));
    }
}

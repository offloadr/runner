using System.Text;
using Offloadr.Runner.Linux;

namespace Offloadr.Runner.Tests;

public sealed class ComfyPromptQueueTests
{
    [Test]
    public void TryParse_ReadsRunningAndPendingPromptIds()
    {
        var queue = ComfyPromptQueue.TryParse(Encoding.UTF8.GetBytes("""
            {
              "queue_running": [[3, "running-1", {}, {}, []]],
              "queue_pending": [[4, "pending-1", {}, {}, []], [5, "pending-2", {}, {}, []], ["malformed"]]
            }
            """));

        Assert.That(queue, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(queue!.Running, Is.EquivalentTo(new[] { "running-1" }));
            Assert.That(queue.Pending, Is.EquivalentTo(new[] { "pending-1", "pending-2" }));
            Assert.That(queue.Contains("pending-2"), Is.True);
            Assert.That(queue.Contains("finished"), Is.False);
        });
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{\"queue_running\": []}")]
    [TestCase("{\"queue_running\": [], \"queue_pending\": {}}")]
    public void TryParse_UnknownShapeIsUnknown(string json)
        => Assert.That(ComfyPromptQueue.TryParse(Encoding.UTF8.GetBytes(json)), Is.Null);

    [TestCase("interrupt", "{\"prompt_id\":\"p-1\"}", "p-1")]
    [TestCase("/api/interrupt", "{\"prompt_id\":\"p-1\"}", "p-1")]
    [TestCase("api/jobs/p-2/cancel", "", "p-2")]
    [TestCase("api/jobs/cancel", "{\"job_ids\":[\"p-3\",\"p-4\"]}", "p-3,p-4")]
    [TestCase("api/jobs/cancel", "{\"prompt_ids\":[\"p-5\"]}", "p-5")]
    [TestCase("api/interrupt", "", "")]
    [TestCase("api/interrupt", "{}", "")]
    [TestCase("api/interrupt", "garbage", "")]
    [TestCase("api/queue", "{\"delete\":[\"p-1\"]}", "")]
    [TestCase("api/prompt", "{\"prompt_id\":\"p-1\"}", "")]
    public void CancelTargets_NameThePromptsACancelControlTargets(string path, string body, string expected)
        => Assert.That(
            string.Join(",", ComfyCancelTargets.PromptIds(path, Encoding.UTF8.GetBytes(body))),
            Is.EqualTo(expected));
}

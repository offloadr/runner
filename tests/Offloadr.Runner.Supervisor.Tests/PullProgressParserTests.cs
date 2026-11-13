using Offloadr.Runner.Supervisor.Containers;

namespace Offloadr.Runner.Supervisor.Tests;

public class PullProgressParserTests
{
    // `docker pull nginx:1.27-alpine | cat` with Docker 29.9 (overlay2), as printed.
    private const string DockerTranscript = """
        1.27-alpine: Pulling from library/nginx
        f18232174bc9: Pulling fs layer
        61ca4f733c80: Pulling fs layer
        b464cfdf2a63: Pulling fs layer
        d7e507024086: Pulling fs layer
        81bd8ed7ec67: Pulling fs layer
        197eb75867ef: Pulling fs layer
        34a64644b756: Pulling fs layer
        39c2ddfd6010: Pulling fs layer
        34a64644b756: Waiting
        39c2ddfd6010: Waiting
        197eb75867ef: Waiting
        81bd8ed7ec67: Waiting
        d7e507024086: Waiting
        b464cfdf2a63: Verifying Checksum
        b464cfdf2a63: Download complete
        d7e507024086: Verifying Checksum
        d7e507024086: Download complete
        61ca4f733c80: Verifying Checksum
        61ca4f733c80: Download complete
        f18232174bc9: Verifying Checksum
        f18232174bc9: Download complete
        81bd8ed7ec67: Download complete
        197eb75867ef: Verifying Checksum
        197eb75867ef: Download complete
        34a64644b756: Verifying Checksum
        34a64644b756: Download complete
        f18232174bc9: Pull complete
        61ca4f733c80: Pull complete
        b464cfdf2a63: Pull complete
        d7e507024086: Pull complete
        81bd8ed7ec67: Pull complete
        197eb75867ef: Pull complete
        34a64644b756: Pull complete
        39c2ddfd6010: Verifying Checksum
        39c2ddfd6010: Download complete
        39c2ddfd6010: Pull complete
        Digest: sha256:65645c7bb6a0661892a8b03b89d0743208a18dd2f3f17a54ef4b76fb8e2f2a10
        Status: Downloaded newer image for nginx:1.27-alpine
        docker.io/library/nginx:1.27-alpine
        """;

    [Test]
    public void Docker_CountsEveryLayerThroughDownloadAndUnpacking()
    {
        var parser = new PullProgressParser();
        var seen = new List<PullProgress>();
        foreach (var line in DockerTranscript.Split('\n'))
        {
            if (parser.Accept(line.TrimEnd('\r')))
            {
                seen.Add(parser.Current);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(seen[7], Is.EqualTo(new PullProgress(8, 0, 0)), "All eight layers are known first.");
            Assert.That(seen, Has.Some.EqualTo(new PullProgress(8, 7, 0)));
            Assert.That(seen, Has.Some.EqualTo(new PullProgress(8, 7, 7)));
            Assert.That(parser.Current, Is.EqualTo(new PullProgress(8, 8, 8)));
            Assert.That(parser.Current.Fraction, Is.EqualTo(1.0));
            Assert.That(seen.Select(progress => progress.Fraction), Is.Ordered, "Progress never goes back.");
        });
    }

    [Test]
    public void Docker_LayersAlreadyHereCountAsDone()
    {
        var parser = new PullProgressParser();
        parser.Accept("f18232174bc9: Already exists");
        parser.Accept("61ca4f733c80: Pulling fs layer");
        parser.Accept("61ca4f733c80: Downloading");

        Assert.Multiple(() =>
        {
            Assert.That(parser.Current, Is.EqualTo(new PullProgress(2, 1, 1)));
            Assert.That(parser.Current.Summary, Is.EqualTo("Downloaded 1 of 2 layers"));
        });
    }

    [Test]
    public void Docker_ALateStatusForALayerDoesNotUndoIt()
    {
        var parser = new PullProgressParser();
        parser.Accept("f18232174bc9: Pull complete");

        Assert.That(parser.Accept("f18232174bc9: Verifying Checksum"), Is.False);
        Assert.That(parser.Current, Is.EqualTo(new PullProgress(1, 1, 1)));
    }

    [Test]
    public void Podman_CountsCopiedBlobs()
    {
        var parser = new PullProgressParser();
        foreach (var line in new[]
        {
            "Trying to pull ghcr.io/offloadr/runner-comfyui:latest...",
            "Getting image source signatures",
            "Copying blob sha256:f18232174bc91741fdf3da96d85011092101a032a93a388b79e99e69c2d5c870",
            "Copying blob sha256:61ca4f733c802afd9e05a32f0de0361b6d713b8b53292dc15fb093229f648674",
            "Copying blob f18232174bc9 done   | ",
            "Copying blob 61ca4f733c80 skipped: already exists  ",
            "Copying config sha256:aaaaaaaaaaaa done   | ",
            "Writing manifest to image destination",
        })
        {
            parser.Accept(line);
        }

        Assert.That(parser.Current, Is.EqualTo(new PullProgress(2, 2, 2)));
    }

    [Test]
    public void Podman_ThatOnlyAnnouncesBlobs_StaysUnmeasuredUntilTheManifest()
    {
        var parser = new PullProgressParser();
        parser.Accept("Trying to pull ghcr.io/offloadr/runner-comfyui:latest...");
        parser.Accept("Getting image source signatures");
        parser.Accept("Copying blob sha256:f18232174bc91741fdf3da96d85011092101a032a93a388b79e99e69c2d5c870");
        parser.Accept("Copying blob sha256:61ca4f733c802afd9e05a32f0de0361b6d713b8b53292dc15fb093229f648674");
        parser.Accept("Copying config sha256:aaaaaaaaaaaa");

        Assert.Multiple(() =>
        {
            Assert.That(parser.Current.Layers, Is.EqualTo(2));
            Assert.That(parser.Current.Fraction, Is.Null, "No bar that would sit at zero for the whole download.");
            Assert.That(parser.Current.Summary, Is.EqualTo("Downloading 2 layers…"));
        });

        Assert.That(parser.Accept("Writing manifest to image destination"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(parser.Current, Is.EqualTo(new PullProgress(2, 2, 2)));
            Assert.That(parser.Current.Fraction, Is.EqualTo(1.0));
        });
    }

    [Test]
    public void OtherLines_AreIgnored()
    {
        var parser = new PullProgressParser();

        Assert.Multiple(() =>
        {
            Assert.That(parser.Accept("latest: Pulling from offloadr/runner-comfyui"), Is.False);
            Assert.That(parser.Accept("Digest: sha256:65645c7bb6a0661892a8b03b89d0743208a18dd2f3f17a54ef4b76fb8e2f2a10"), Is.False);
            Assert.That(parser.Accept("Error response from daemon: manifest unknown"), Is.False);
            Assert.That(parser.Current.Fraction, Is.Null);
            Assert.That(parser.Current.Summary, Is.EqualTo("Looking up the image…"));
        });
    }
}

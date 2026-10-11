using System.Xml.Linq;

namespace Offloadr.Runner.Supervisor.Tests;

/// <summary>Keeps the container-only runtime out of everything that runs on a desktop.</summary>
public class ProjectBoundaryTests
{
    [TestCase("Offloadr.Runner.Core")]
    [TestCase("Offloadr.Runner.Supervisor")]
    [TestCase("Offloadr.Runner.UI")]
    [TestCase("Offloadr.Runner.Desktop")]
    public void DoesNotReferenceTheContainerRuntime(string project)
    {
        var references = TransitiveReferences(ProjectFile(project));

        Assert.That(references, Has.None.EqualTo("Offloadr.Runner.Linux").And.None.EqualTo("Offloadr.Runner.Agent"));
    }

    private static HashSet<string> TransitiveReferences(string projectFile)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([projectFile]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var include in XDocument.Load(current).Descendants("ProjectReference").Select(element => (string?)element.Attribute("Include")))
            {
                if (include is null)
                {
                    continue;
                }

                var referenced = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (found.Add(Path.GetFileNameWithoutExtension(referenced)))
                {
                    pending.Push(referenced);
                }
            }
        }

        return found;
    }

    private static string ProjectFile(string project)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "runner.slnx")))
            {
                return Path.Combine(directory.FullName, "src", project, project + ".csproj");
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

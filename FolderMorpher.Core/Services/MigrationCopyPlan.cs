using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services;


// Resolve original source containment globally, independently of where a node was moved.
internal static class MigrationCopyPlan
{
    internal static List<MigrationCopyUnit> Resolve(IEnumerable<FolderMorpher.Models.MigrationWavePlan> waves, string targetRoot)
    {
        var assignments = waves.SelectMany(w => Flatten(w.TargetNodes).Select(n => (Node: n, Wave: w.WaveNumber))).ToList();
        var allSources = assignments.SelectMany(x => x.Node.MappedSourcePaths).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<MigrationCopyUnit>();
        foreach (var assignment in assignments)
        {
            var names = new Stack<string>();
            for (var node = assignment.Node; node != null; node = node.Parent)
            {
                if (string.IsNullOrWhiteSpace(node.Name) || node.Name is "." or ".." || node.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidOperationException("Invalid destination folder name.");
                names.Push(node.Name);
            }
            string destination = Path.GetFullPath(Path.Combine(new[] { targetRoot }.Concat(names).ToArray()));
            foreach (string source in assignment.Node.MappedSourcePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath))
            {
                if (allSources.Any(p => IsWithin(p, destination) || IsWithin(destination, p) || string.Equals(p, destination, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Source and destination overlap.");
                result.Add(new(source, destination, allSources.Where(p => IsWithin(source, p)).ToList(), assignment.Wave));
            }
        }
        return result.DistinctBy(u => (u.Source.ToUpperInvariant(), u.Destination.ToUpperInvariant())).ToList();
    }

    internal static bool IsWithin(string parent, string child) => child.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SimFolderNode> Flatten(IEnumerable<SimFolderNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}

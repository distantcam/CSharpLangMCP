using DotNetMCP.Tools;
using Microsoft.CodeAnalysis;

namespace DotNetMCP.Tests;

/// <summary>Covers <c>csharp_type_hierarchy</c> against a small hierarchy added in memory.</summary>
public class TypeHierarchyTests
{
    private const string Probe = """
        namespace DotNetMCP.Probe;

        internal interface IShape;
        internal interface ISolid : IShape;
        internal abstract class Shape : IShape;
        internal class Circle : Shape;
        internal sealed class Ring : Circle;
        internal struct Point : IShape;
        """;

    private static async Task<string> DescribeAsync(string typeName)
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();
        var project = solution.Projects.First(p =>
            p.Name.StartsWith("DotNetMCP", StringComparison.Ordinal) &&
            !p.Name.Contains("Tests", StringComparison.Ordinal));

        project = project.AddDocument("HierarchyProbe.cs", Probe).Project;
        var type = (await project.GetCompilationAsync())!.GetTypeByMetadataName("DotNetMCP.Probe." + typeName)!;

        return await NavigationTools.DescribeHierarchyAsync(project.Solution, type, sourceOnly: true, maxResults: 100, CancellationToken.None);
    }

    /// <summary>Lines with their indentation, so tree shape can be asserted on.</summary>
    private static string[] Lines(string result) => result.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Test]
    public async Task ClassShowsBaseChainAboveAndTreeBelow()
    {
        var lines = Lines(await DescribeAsync("Circle"));

        await Assert.That(lines).Contains("  object");
        await Assert.That(lines).Contains("    DotNetMCP.Probe.Shape");
        await Assert.That(lines).Contains("      DotNetMCP.Probe.Circle   <- this type");
        await Assert.That(lines).Contains("  DotNetMCP.Probe.IShape   (inherited)");
        await Assert.That(lines).Contains("  DotNetMCP.Probe.Ring");
    }

    [Test]
    public async Task InterfaceNestsDerivedTypesUnderTheirNearestAncestor()
    {
        var lines = Lines(await DescribeAsync("IShape"));

        // Direct implementers sit at the top level; Circle and Ring hang under Shape, not IShape.
        await Assert.That(lines).Contains("  DotNetMCP.Probe.Shape");
        await Assert.That(lines).Contains("  DotNetMCP.Probe.Point");
        await Assert.That(lines).Contains("  DotNetMCP.Probe.ISolid");
        await Assert.That(lines).Contains("    DotNetMCP.Probe.Circle");
        await Assert.That(lines).Contains("      DotNetMCP.Probe.Ring");
    }

    [Test]
    public async Task SealedTypeSaysSo()
    {
        var result = await DescribeAsync("Ring");

        await Assert.That(result).Contains("Derived types: none (sealed).");
    }
}

using DotNetMCP.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetMCP.Tests;

/// <summary>
/// Covers the shared rendering the editing tools route through, via the rename tool.
/// </summary>
/// <remarks>
/// Preview mode only: a passing test must not rewrite the repository it is reading.
/// </remarks>
public class ChangePreviewTests
{
    [Test]
    public async Task PreviewsEditsAcrossFilesWithoutWritingThem()
    {
        var (service, _) = await WorkspaceFixture.GetAsync();
        var tool = new EditTools(NullLogger<EditTools>.Instance, service);

        var source = Path.Combine(
            WorkspaceFixture.RepositoryRoot, "src", "DotNetMCP", "Roslyn", "SymbolFormatter.cs");
        var before = await File.ReadAllTextAsync(source);

        var result = await tool.RenameAsync(symbol: "SymbolFormatter.Truncate", newName: "Shorten");

        await Assert.That(result).DoesNotStartWith("Error:");
        await Assert.That(result).Contains("edit(s) across");
        await Assert.That(result).Contains("Nothing was written.");
        await Assert.That(result).DoesNotContain("Applied");

        // The preview lists a path and a line per edit, which is the half worth getting wrong.
        await Assert.That(result).Contains("SymbolFormatter.cs: ");

        await Assert.That(await File.ReadAllTextAsync(source)).IsEqualTo(before);
    }

    [Test]
    public async Task SaysSoWhenThereIsNothingToChange()
    {
        var (service, _) = await WorkspaceFixture.GetAsync();
        var tool = new EditTools(NullLogger<EditTools>.Instance, service);

        var source = Path.Combine(
            WorkspaceFixture.RepositoryRoot, "src", "DotNetMCP", "Roslyn", "SymbolFormatter.cs");

        var result = await tool.FormatAsync(source);

        await Assert.That(result).DoesNotStartWith("Error:");
        await Assert.That(result).Contains("already correctly formatted");
    }
}

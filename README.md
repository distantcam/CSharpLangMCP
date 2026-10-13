# CSharpLangMCP

An MCP server that exposes C# semantic analysis — diagnostics, symbol search, navigation, references, type inspection and rename — as MCP tools, built directly on [Roslyn](https://github.com/dotnet/roslyn).

Communicates over **stdio**. Advertised server name: `csharplang-mcp`.

## Prerequisites

The **.NET 10 SDK**. Nothing else — no language server to install separately.

Roslyn evaluates project files through its own out-of-process MSBuild build host, which is why the SDK is required at runtime and not just to build.

## Install

```bash
dotnet tool install -g CSharpLangMCP
```

The command is `csharplang-mcp`. Nothing to unzip, no absolute paths, and no second runtime to install — the SDK is already a prerequisite, so anything that can run this server already has `dotnet`.

## Build

```bash
dotnet pack src/CSharpLangMCP/CSharpLangMCP.csproj -c Release
```

Output: `src\artifacts\package\release\CSharpLangMCP.0.1.0.nupkg`. To try it before publishing:

```bash
dotnet tool install -g CSharpLangMCP --add-source src/artifacts/package/release
dotnet tool uninstall -g CSharpLangMCP
```

The package is a RID-less, framework-dependent .NET tool, so one `.nupkg` covers every platform. It carries the `BuildHost-net472\` and `BuildHost-netcore\` folders beside the assembly, which is where Roslyn looks for them — it evaluates project files by launching one of them as a separate process.

## Tests

```bash
dotnet run --project src/CSharpLangMCP.Tests/CSharpLangMCP.Tests.csproj
```

TUnit builds the test project as an executable, so running it directly is the native path. `dotnet test` needs the .NET 10 Microsoft.Testing.Platform opt-in in `global.json` and is not wired up here.

The tests load this repository as their own fixture, so they exercise the real symbol graph rather than a synthetic one.

## Configuration

### Claude Code

```bash
claude mcp add csharplang -- csharplang-mcp
```

### Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json` (create it if it does not exist) and **merge** the `mcpServers` key into the existing top-level object — do not replace the file, it holds your other Claude Desktop settings.

```json
{
  "mcpServers": {
    "csharplang": {
      "command": "csharplang-mcp",
      "args": []
    }
  }
}
```

Then **fully quit Claude Desktop from the system tray** and relaunch. Closing the window leaves the process running, and the server will not be reloaded.

### Without installing

`dnx` runs a NuGet tool straight from the feed, the way `npx` does:

```json
{
  "mcpServers": {
    "csharplang": {
      "command": "dnx",
      "args": ["CSharpLangMCP", "--yes"]
    }
  }
}
```

First launch downloads the package, so expect that call to be slower.

## Tools

`csharp_load_workspace` accepts a directory, or a path to a `.slnx`, `.sln`, `.slnf` or `.csproj` file. Calling it first is fastest, but the other tools will discover and load a workspace on their own — walking up from the file you passed, or from the server's working directory.

Positions are **0-based on input** and 1-based in the output.

### Addressing symbols

Most tools take a `symbol` argument rather than a file position. It accepts either:

- an **id** as returned by `csharp_find_symbol`, e.g. `M:CSharpLangMCP.Roslyn.SymbolResolver.ResolveAsync(Microsoft.CodeAnalysis.Solution,System.String,System.Threading.CancellationToken)` — exact and unambiguous; or
- a **name**, plain or partially qualified: `ResolveAsync`, or `SymbolResolver.ResolveAsync` to narrow it.

If a name matches several symbols, the tool returns the candidates with their ids instead of guessing. Every tool also accepts `filePath` + `line` + `character` as a fallback.

### Workspace

| Tool | Purpose |
|---|---|
| `csharp_load_workspace` | Load a solution or project. Reports the projects loaded and any that were skipped. |
| `csharp_unload_workspace` | Release memory and file handles. Roslyn memory-maps referenced assemblies, so call this if a rebuild reports locked files. |

### Diagnostics

| Tool | Purpose |
|---|---|
| `csharp_diagnostics` | Compiler errors and warnings. `scope` is `solution`, a project name, or a `.cs` file path. Each result includes the offending source line. Pass `includeAnalyzers=true` to also run the project's analyzers. |

By default this is the compiler alone, which is fast but is **not** what `dotnet build` reports: a project with `AnalysisMode` or `EnforceCodeStyleInBuild` set fails its build on `CAxxxx` and `IDExxxx` rules that no compiler diagnostic covers. `includeAnalyzers=true` runs the analyzers the project actually configures, read from the project file by MSBuild — no build, no external process. Severities come out as the build reports them, so a project setting `TreatWarningsAsErrors` gets analyzer *errors*, not warnings.

### Navigation

| Tool | Purpose |
|---|---|
| `csharp_find_symbol` | Search the solution for declarations by name or pattern. The usual starting point. |
| `csharp_symbol_info` | Signature, accessibility, modifiers, base types, XML documentation and declaration sites. |
| `csharp_references` | Every use of a symbol, grouped by file, with the source line at each site. |
| `csharp_implementations` | Interface implementers, derived types and overrides. Defaults to source only; `sourceOnly=false` includes referenced assemblies. |
| `csharp_type_hierarchy` | A type's base class chain and interfaces above it, and the tree of derived and implementing types below it, in one call. |
| `csharp_callers` | The methods that call a method or access a property or field. |
| `csharp_callees` | The reverse: what a method, constructor or property calls, in body order, including inside lambdas and local functions. `sourceOnly=true` hides BCL and package calls. |
| `csharp_unused_symbols` | Types and members nothing references, for a solution, project or file. Skips overrides, interface implementations, constructors, entry points and attributed symbols; public symbols only with `includePublic=true`. |

### Structure

| Tool | Purpose |
|---|---|
| `csharp_outline` | The declarations in a file as a nested outline. |
| `csharp_type_members` | Members of any type, including ones from referenced assemblies such as the BCL or a NuGet package. |
| `csharp_generated_sources` | List the files source generators produce, or read them by passing `hintName`. They exist only in memory, so this is the only way to see them without building. |
| `csharp_decompile` | Decompile a type or member from a referenced assembly back to C#. BCL reference assemblies are swapped for the running runtime's implementation, so you get real method bodies rather than stubs. |

### Code actions

| Tool | Purpose |
|---|---|
| `csharp_code_actions` | List the quick fixes and refactorings available at a position or line range — the same set an IDE lightbulb offers. |
| `csharp_apply_code_action` | Apply one of them by its exact title. |

Grouped actions are flattened, so a group appears as its individually applicable children (`Generate type 'List' > Generate class 'List'`). Titles are unique within a call and are the handle you pass to `csharp_apply_code_action`.

Refactorings are the slower half — every provider has to be asked. Pass `includeRefactorings=false` for a fixes-only call.

`includeAnalyzers=true` also considers the file's analyzer diagnostics, which is what makes the `IDExxxx` fixes — add braces, use expression body, remove unnecessary usings — available at all. Pass the same value to `csharp_apply_code_action`. `CAxxxx` diagnostics are reported by `csharp_diagnostics`, but their fixers ship in the NetAnalyzers package rather than the Roslyn Features assemblies this server composes, so no fix is offered for them.

### Editing

The editing and code action tools **preview by default** and write only when passed `apply=true`.

| Tool | Purpose |
|---|---|
| `csharp_rename` | Rename a symbol across the solution. |
| `csharp_format` | Format a file using the solution's `.editorconfig`. |

## Notes

- **Cold start.** The first workspace load takes a few seconds on a small solution and considerably longer on a large one. It happens on the first tool call if you did not call `csharp_load_workspace` yourself.
- **Memory.** A large solution can hold well over a gigabyte. `csharp_unload_workspace` releases it.
- **Staleness.** Files edited on disk are picked up automatically on the next tool call. Adding or deleting a `.cs` file, or editing a solution, project or `Directory.*.props`/`.targets` file, triggers an automatic reload on the next call, which costs as much as the first load.
- **Analyzer cost.** `includeAnalyzers=true` is off by default because it is not free: a file-scoped call is scoped to that one syntax tree, but a solution-scoped run asks all ~400 analyzers about every project. On this repository that is about a second; on a large solution it is the difference between a quick answer and a slow one, so name a project rather than `solution` where you can.
- **Unsaved buffers.** Tools that take a `content` argument analyze that text instead of what is on disk.

## Troubleshooting

Logs go to stderr, which Claude Desktop captures at `%APPDATA%\Claude\logs\mcp-server-dotnet.log`.

For more detail, raise the log level from `Warning` to `Debug`:

```json
"args": ["--verbose"]
```

Quit Claude Desktop before reinstalling the tool — while the server is running, Desktop holds a lock on the installed files and the install will fail.

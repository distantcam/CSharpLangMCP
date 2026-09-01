# DotNetMCP

An MCP server that exposes C# semantic analysis — diagnostics, symbol search, navigation, references, type inspection and rename — as MCP tools, built directly on [Roslyn](https://github.com/dotnet/roslyn).

Communicates over **stdio**. Advertised server name: `dotnet-mcp`.

## Prerequisites

The **.NET 10 SDK**. Nothing else — no language server to install separately.

Roslyn evaluates project files through its own out-of-process MSBuild build host, which is why the SDK is required at runtime and not just to build.

## Build

```bash
dotnet publish src/DotNetMCP/DotNetMCP.csproj -c Release -r win-x64 -o dist
```

Output: `dist\DotNetMCP.exe` — a single-file build with every dependency bundled in — plus the `BuildHost-net472\` and `BuildHost-netcore\` folders. **Those two folders have to travel with the exe.** Roslyn evaluates project files by launching one of them as a separate process, so they cannot live inside the bundle; it looks for them next to the executable.

Single-file publish requires a runtime identifier; swap `win-x64` for `linux-x64` or `osx-arm64` elsewhere. The build stays framework-dependent — the .NET 10 SDK is a runtime prerequisite regardless, so bundling a private runtime would only add ~70 MB.

## Tests

```bash
dotnet run --project src/DotNetMCP.Tests/DotNetMCP.Tests.csproj
```

TUnit builds the test project as an executable, so running it directly is the native path. `dotnet test` needs the .NET 10 Microsoft.Testing.Platform opt-in in `global.json` and is not wired up here.

The tests load this repository as their own fixture, so they exercise the real symbol graph rather than a synthetic one.

## Configuration

### Claude Code

```bash
claude mcp add dotnet -- path/to/DotNetMCP.exe
```

### Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json` (create it if it does not exist) and **merge** the `mcpServers` key into the existing top-level object — do not replace the file, it holds your other Claude Desktop settings.

```json
{
  "mcpServers": {
    "dotnet": {
      "command": "C:\\src\\ai\\DotNetMCP\\dist\\DotNetMCP.exe",
      "args": []
    }
  }
}
```

The path must be absolute, and backslashes must be doubled — JSON escaping.

Then **fully quit Claude Desktop from the system tray** and relaunch. Closing the window leaves the process running, and the server will not be reloaded.

## Tools

`csharp_load_workspace` accepts a directory, or a path to a `.slnx`, `.sln`, `.slnf` or `.csproj` file. Calling it first is fastest, but the other tools will discover and load a workspace on their own — walking up from the file you passed, or from the server's working directory.

Positions are **0-based on input** and 1-based in the output.

### Addressing symbols

Most tools take a `symbol` argument rather than a file position. It accepts either:

- an **id** as returned by `csharp_find_symbol`, e.g. `M:DotNetMCP.Roslyn.SymbolResolver.ResolveAsync(Microsoft.CodeAnalysis.Solution,System.String,System.Threading.CancellationToken)` — exact and unambiguous; or
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
| `csharp_diagnostics` | Compiler errors and warnings. `scope` is `solution`, a project name, or a `.cs` file path. Each result includes the offending source line. |

### Navigation

| Tool | Purpose |
|---|---|
| `csharp_find_symbol` | Search the solution for declarations by name or pattern. The usual starting point. |
| `csharp_symbol_info` | Signature, accessibility, modifiers, base types, XML documentation and declaration sites. |
| `csharp_references` | Every use of a symbol, grouped by file, with the source line at each site. |
| `csharp_implementations` | Interface implementers, derived types and overrides. Defaults to source only; `sourceOnly=false` includes referenced assemblies. |
| `csharp_callers` | The methods that call a method or access a property or field. |

### Structure

| Tool | Purpose |
|---|---|
| `csharp_outline` | The declarations in a file as a nested outline. |
| `csharp_type_members` | Members of any type, including ones from referenced assemblies such as the BCL or a NuGet package. |

### Code actions

| Tool | Purpose |
|---|---|
| `csharp_code_actions` | List the quick fixes and refactorings available at a position or line range — the same set an IDE lightbulb offers. |
| `csharp_apply_code_action` | Apply one of them by its exact title. |

Grouped actions are flattened, so a group appears as its individually applicable children (`Generate type 'List' > Generate class 'List'`). Titles are unique within a call and are the handle you pass to `csharp_apply_code_action`.

Refactorings are the slower half — every provider has to be asked. Pass `includeRefactorings=false` for a fixes-only call.

### Editing

The editing and code action tools **preview by default** and write only when passed `apply=true`.

| Tool | Purpose |
|---|---|
| `csharp_rename` | Rename a symbol across the solution. |
| `csharp_format` | Format a file using the solution's `.editorconfig`. |

## Notes

- **Cold start.** The first workspace load takes a few seconds on a small solution and considerably longer on a large one. It happens on the first tool call if you did not call `csharp_load_workspace` yourself.
- **Memory.** A large solution can hold well over a gigabyte. `csharp_unload_workspace` releases it.
- **Staleness.** Files edited on disk are picked up automatically on the next tool call. Changes to *project* files are not — reload the workspace with `csharp_load_workspace`.
- **Unsaved buffers.** Tools that take a `content` argument analyze that text instead of what is on disk.

## Troubleshooting

Logs go to stderr, which Claude Desktop captures at `%APPDATA%\Claude\logs\mcp-server-dotnet.log`.

For more detail, raise the log level from `Warning` to `Debug`:

```json
"args": ["--verbose"]
```

Quit Claude Desktop before re-running `dotnet publish` — while the server is running, Desktop holds a lock on the files in `dist\` and the publish will fail.

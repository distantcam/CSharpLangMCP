# DotNetMCP

An MCP server that exposes C# language-server features — diagnostics, hover, completions, go-to-definition, references, symbols, code actions and rename — as MCP tools, by bridging to [`csharp-ls`](https://github.com/razzmatazz/csharp-language-server).

Communicates over **stdio**. Advertised server name: `dotnet-mcp`.

## Prerequisites

`csharp-ls` must be installed **globally** — a local tool manifest install will not be found, because the server launches the `csharp-ls` executable directly:

```
dotnet tool install --global csharp-ls --version 0.27.0
```

It is looked up on `PATH`, then at `%USERPROFILE%\.dotnet\tools\csharp-ls.exe`, then `/usr/local/bin/csharp-ls` and `/usr/bin/csharp-ls`.

Building additionally requires the .NET 10 SDK. Running does not — see below.

## Build

`dotnet publish` produces a **self-contained, single-file win-x64 executable**, so the machine running it does not need the .NET runtime installed:

```
dotnet publish src/DotNetMCP/DotNetMCP.csproj -c Release -o dist
```

Output: `dist\DotNetMCP.exe` (~74 MB).

# Configuration

## Claude Code

`claude mcp add dotnet -- path/to/DotNetMCP.exe`

## Claude Desktop

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

### First call

Call `csharp_set_workspace` before anything else — the other tools have no workspace to operate on until you do. It takes a **directory** (the folder containing your solution), not a path to a `.sln`/`.slnx`/`.csproj` file; passing a file returns `Error: Directory does not exist`.

All other tools take absolute file paths, and `line`/`character` arguments are **0-based**.

### Troubleshooting

Logs go to stderr, which Claude Desktop captures at `%APPDATA%\Claude\logs\mcp-server-dotnet.log`.

For more detail, raise the log level from `Warning` to `Debug`:

```json
"args": ["--verbose"]
```

Quit Claude Desktop before re-running `dotnet publish` — while the server is running, Desktop holds a lock on `dist\DotNetMCP.exe` and the publish will fail.

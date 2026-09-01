using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetMCP.Lsp;

/// <summary>
/// Source-generated serialization metadata for the LSP wire protocol.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately no <see cref="JsonStringEnumConverter"/>: every LSP enum is numeric on the
/// wire (triggerKind: 1, severity: 2, ...), and serializing them as strings makes the server
/// reject the request. Source generation defaults to numeric enums, which is what we want.
/// </para>
/// <para>
/// <see cref="JsonRpcRequest.Params"/> is declared as <see cref="object"/>, so it is written
/// using the runtime type of the value. Every type ever passed as request or notification
/// params must therefore be registered below, or serialization throws at run time.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// Envelope
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(JsonElement))]

// Outbound params (reached through the object-typed Params property)
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializedParams))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(DidSaveTextDocumentParams))]
[JsonSerializable(typeof(HoverParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(DefinitionParams))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(DocumentSymbolParams))]
[JsonSerializable(typeof(CodeActionParams))]
[JsonSerializable(typeof(RenameParams))]

// Inbound results and server notifications
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionItem[]))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(Location[]))]
[JsonSerializable(typeof(DocumentSymbol[]))]
[JsonSerializable(typeof(SymbolInformation[]))]
[JsonSerializable(typeof(CodeAction[]))]
[JsonSerializable(typeof(WorkspaceEdit))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]

// Loosely-typed members (Hover.Contents, Diagnostic.Code, Command.Arguments, ...)
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(object[]))]
internal sealed partial class LspJsonContext : JsonSerializerContext;

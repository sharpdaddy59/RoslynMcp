using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynMcp.Tools;

namespace RoslynMcp;

/// <summary>
/// Runs in front of every tool call. Two jobs, both found by watching small local models drive
/// the server (cpu-llm-bench/roslyn-agent, 2026-11-27):
///
/// 1. Argument coercion. Smaller models routinely send <c>"dryRun": "false"</c>, <c>"line": "12"</c>
///    or an array as a JSON-encoded string. The SDK's binder rejects those with an opaque
///    "An error occurred invoking 'x'", which gives the model nothing to correct, so it retries the
///    same call or gives up. Here such values are converted to the type the schema declares; values
///    that can't be converted produce an error naming the parameter, expected type and value received.
///
/// 2. Auto-load. In 28 of 42 bench runs the model's first call was not load_solution. If no solution
///    is loaded and exactly one candidate can be found, from the call's filePath (nearest ancestor
///    directory containing a .sln/.slnx) or else the server's working directory, it is loaded and the
///    call proceeds, with a note saying so. If the choice is ambiguous, the error lists the candidates.
/// </summary>
public static class ToolCallFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, cancellationToken) =>
    {
        var name = context.Params?.Name ?? "";
        var args = context.Params?.Arguments;

        if (args is { Count: > 0 } && context.MatchedPrimitive is McpServerTool tool)
        {
            var problems = CoerceArguments(tool.ProtocolTool.InputSchema, args);
            if (problems.Count > 0)
            {
                return ErrorResult($"Invalid arguments for '{name}': {string.Join("; ", problems)}.");
            }
        }

        string? autoLoaded = null;
        if (WorkspaceHost.LoadedPath is null && name is not ("load_solution" or ""))
        {
            var filePath = args is not null && args.TryGetValue("filePath", out var fp) && fp.ValueKind == JsonValueKind.String
                ? fp.GetString()
                : null;
            var candidates = FindSolutionCandidates(filePath, Directory.GetCurrentDirectory());
            if (candidates.Count == 1)
            {
                try
                {
                    await WorkspaceHost.LoadAsync(candidates[0], cancellationToken);
                    autoLoaded = candidates[0];
                }
                catch (Exception ex)
                {
                    return ErrorResult($"No solution loaded, and auto-loading {candidates[0]} failed: {ex.Message}. " +
                                       "Call load_solution with the absolute path of a .sln/.slnx/.csproj.");
                }
            }
            else
            {
                return ErrorResult(candidates.Count == 0
                    ? "No solution loaded and none found near the request. Call load_solution with the absolute path " +
                      "of a .sln/.slnx/.csproj first."
                    : "No solution loaded. Call load_solution first with one of: " + string.Join(", ", candidates));
            }
        }

        var result = await next(context, cancellationToken);
        if (autoLoaded is not null)
        {
            result.Content.Add(new TextContentBlock
            {
                Text = ToolJson.Ok(new { note = $"No solution was loaded, so {autoLoaded} was loaded automatically." })
            });
        }
        return result;
    };

    private static CallToolResult ErrorResult(string message) => new()
    {
        // Same shape as every other tool error (ToolJson.Error): a JSON {"error": ...} text result.
        Content = [new TextContentBlock { Text = ToolJson.Error(message) }],
        IsError = true
    };

    /// <summary>
    /// Converts argument values in place to the JSON types the tool's input schema declares, where
    /// the conversion is unambiguous. Returns one message per argument that has the wrong type and
    /// can't be converted. Unknown arguments and values that already match are left alone.
    /// </summary>
    public static List<string> CoerceArguments(JsonElement schema, IDictionary<string, JsonElement> args)
    {
        var problems = new List<string>();
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
        {
            return problems;
        }

        foreach (var key in args.Keys.ToList())
        {
            if (!props.TryGetProperty(key, out var propSchema)) continue;
            var types = DeclaredTypes(propSchema);
            if (types.Count == 0) continue;

            var value = args[key];
            if (Matches(value, types)) continue;

            if (TryConvert(value, types, out var converted))
            {
                args[key] = converted;
            }
            else
            {
                var raw = value.GetRawText();
                if (raw.Length > 60) raw = raw[..60] + "...";
                problems.Add($"'{key}' expects {string.Join(" or ", types.Where(t => t != "null"))}, got {Describe(value.ValueKind)} {raw}");
            }
        }
        return problems;
    }

    private static List<string> DeclaredTypes(JsonElement propSchema)
    {
        if (!propSchema.TryGetProperty("type", out var t)) return [];
        return t.ValueKind switch
        {
            JsonValueKind.String => [t.GetString()!],
            JsonValueKind.Array => t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToList(),
            _ => []
        };
    }

    private static bool Matches(JsonElement v, List<string> types) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true, // optional parameters; the tool decides
        JsonValueKind.String => types.Contains("string"),
        JsonValueKind.True or JsonValueKind.False => types.Contains("boolean"),
        JsonValueKind.Number => types.Contains("number") ||
                                (types.Contains("integer") && v.TryGetInt64(out _)),
        JsonValueKind.Array => types.Contains("array"),
        JsonValueKind.Object => types.Contains("object"),
        _ => false
    };

    private static bool TryConvert(JsonElement v, List<string> types, out JsonElement converted)
    {
        converted = default;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()!.Trim();
            if (types.Contains("boolean") && bool.TryParse(s, out var b))
            {
                converted = JsonSerializer.SerializeToElement(b);
                return true;
            }
            if (types.Contains("integer") && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                converted = JsonSerializer.SerializeToElement(l);
                return true;
            }
            if (types.Contains("number") && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                converted = JsonSerializer.SerializeToElement(d);
                return true;
            }
            if ((types.Contains("array") && s.StartsWith('[')) || (types.Contains("object") && s.StartsWith('{')))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    var kind = doc.RootElement.ValueKind;
                    if ((kind == JsonValueKind.Array && types.Contains("array")) ||
                        (kind == JsonValueKind.Object && types.Contains("object")))
                    {
                        converted = doc.RootElement.Clone();
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // fall through: reported as a problem
                }
            }
            return false;
        }

        // A whole number sent as 12.0 for an integer parameter.
        if (v.ValueKind == JsonValueKind.Number && types.Contains("integer") &&
            v.TryGetDouble(out var dbl) && dbl == Math.Floor(dbl) && Math.Abs(dbl) < long.MaxValue)
        {
            converted = JsonSerializer.SerializeToElement((long)dbl);
            return true;
        }

        // A scalar where a string is wanted (newName: 123, interfaceName: true).
        if (types.Contains("string") && v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            converted = JsonSerializer.SerializeToElement(v.GetRawText());
            return true;
        }
        return false;
    }

    private static string Describe(JsonValueKind k) => k switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => k.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Solutions a call could plausibly mean. From a filePath: walk up from its directory and return
    /// the .sln/.slnx files in the nearest directory that has any. Otherwise the working directory's
    /// .sln/.slnx files, or, if there are none, its single .csproj.
    /// </summary>
    public static IReadOnlyList<string> FindSolutionCandidates(string? filePath, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var full = Path.GetFullPath(filePath);
                var dir = File.Exists(full) || Path.HasExtension(full) ? Path.GetDirectoryName(full) : full;
                for (var d = dir is null ? null : new DirectoryInfo(dir); d is not null; d = d.Parent)
                {
                    if (!d.Exists) continue;
                    var found = Solutions(d.FullName);
                    if (found.Count > 0) return found;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // malformed path: fall back to the working directory
            }
        }

        if (!Directory.Exists(workingDirectory)) return [];
        var here = Solutions(workingDirectory);
        if (here.Count > 0) return here;
        var projects = Directory.GetFiles(workingDirectory, "*.csproj");
        return projects.Length == 1 ? projects : [];
    }

    private static List<string> Solutions(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
}

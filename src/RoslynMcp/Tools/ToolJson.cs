using System.Text.Json;

namespace RoslynMcp.Tools;

internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Ok(object result) => JsonSerializer.Serialize(result, Options);

    public static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, Options);

    /// <summary>
    /// Uniform guard: tool bodies throw freely (no solution loaded, file not in
    /// solution, symbol not found) and the caller gets a structured error instead
    /// of an MCP protocol fault.
    /// </summary>
    public static async Task<string> GuardAsync(Func<Task<object>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }
}

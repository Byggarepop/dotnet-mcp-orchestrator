using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace McpOrchestrator.DemoMcp;

/// <summary>
/// Stand-in for a code-generation MCP server. Generates a trivial C# class from a name
/// and an optional comma-separated field list — enough to demonstrate routing to a
/// second, distinct capability.
/// </summary>
[McpServerToolType]
public sealed class CodegenTools
{
    /// <summary>Emits a sealed C# class named <paramref name="className"/> with a string property per field.</summary>
    [McpServerTool(Name = "generate_class")]
    [Description("Generate a simple C# class. Provide a class name and an optional comma-separated list of fields.")]
    public static string GenerateClass(
        [Description("The class name, e.g. 'Customer'.")] string className,
        [Description("Optional comma-separated fields, e.g. 'Id, Name, Email'.")] string? fields = null)
    {
        var safeName = string.IsNullOrWhiteSpace(className) ? "Generated" : className.Trim();

        var sb = new StringBuilder();
        sb.AppendLine($"public sealed class {safeName}");
        sb.AppendLine("{");

        var fieldList = (fields ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var field in fieldList)
        {
            sb.AppendLine($"    public string {field} {{ get; set; }} = string.Empty;");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}

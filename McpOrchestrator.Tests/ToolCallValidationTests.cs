using System.Text.Json;
using McpOrchestrator.Orchestration;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Unit tests for the pure pre-binding validation/aliasing in <see cref="ToolCallValidation"/>:
/// descriptive errors for missing/unknown/wrongly-typed parameters, and rescue of predictable
/// parameter-name synonyms like 'args' for 'arguments'.
/// </summary>
public sealed class ToolCallValidationTests
{
    /// <summary>The route tool's schema shape: two typed strings plus the untyped 'arguments'.</summary>
    private static readonly JsonElement RouteSchema = Json("""
        {
          "type": "object",
          "properties": {
            "capability": { "type": "string" },
            "tool": { "type": "string" },
            "arguments": { "description": "Arguments object." }
          },
          "required": ["capability", "tool", "arguments"]
        }
        """);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void Missing_required_parameter_is_named_with_expected_shape()
    {
        var result = ToolCallValidation.ValidateAndNormalize(
            RouteSchema, Args("""{ "capability": "jira", "tool": "get_issue" }"""));

        Assert.Equal(
            "Missing required parameter 'arguments'. " +
            "Expected shape: {capability: string, tool: string, arguments: object}.",
            result.Error);
    }

    [Fact]
    public void Missing_required_with_unknown_parameter_names_both()
    {
        // 'argz' is not in the alias table, so it stays unknown and 'arguments' stays missing.
        var result = ToolCallValidation.ValidateAndNormalize(
            RouteSchema, Args("""{ "capability": "jira", "tool": "get_issue", "argz": {} }"""));

        Assert.Equal(
            "Missing required parameter 'arguments' (received unknown parameter 'argz'). " +
            "Expected shape: {capability: string, tool: string, arguments: object}.",
            result.Error);
    }

    [Theory]
    [InlineData("args")]
    [InlineData("params")]
    [InlineData("parameters")]
    [InlineData("input")]
    [InlineData("payload")]
    public void Synonyms_for_arguments_are_aliased_and_recorded(string synonym)
    {
        var result = ToolCallValidation.ValidateAndNormalize(
            RouteSchema,
            Args($$"""{ "capability": "jira", "tool": "get_issue", "{{synonym}}": { "issueKey": "PROJ-1" } }"""));

        Assert.Null(result.Error);
        var alias = Assert.Single(result.AppliedAliases);
        Assert.Equal(synonym, alias.Synonym);
        Assert.Equal("arguments", alias.Canonical);
        Assert.Equal("PROJ-1",
            result.NormalizedArguments!["arguments"].GetProperty("issueKey").GetString());
        Assert.False(result.NormalizedArguments.ContainsKey(synonym));
    }

    [Fact]
    public void Alias_is_not_applied_when_the_canonical_parameter_is_present()
    {
        var result = ToolCallValidation.ValidateAndNormalize(
            RouteSchema,
            Args("""{ "capability": "jira", "tool": "get_issue", "arguments": {}, "args": {} }"""));

        // 'args' stays what it is — an unknown parameter — instead of clobbering 'arguments'.
        Assert.Empty(result.AppliedAliases);
        Assert.Equal(
            "Unknown parameter 'args'. " +
            "Expected shape: {capability: string, tool: string, arguments: object}.",
            result.Error);
    }

    [Fact]
    public void Wrongly_typed_parameter_is_named_with_both_types()
    {
        var result = ToolCallValidation.ValidateAndNormalize(
            RouteSchema, Args("""{ "capability": "jira", "tool": 42, "arguments": {} }"""));

        Assert.Equal(
            "Parameter 'tool' must be a string (received a number). " +
            "Expected shape: {capability: string, tool: string, arguments: object}.",
            result.Error);
    }

    [Fact]
    public void Optional_parameters_are_marked_in_the_expected_shape()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "limit": { "type": "integer" }
              },
              "required": ["name"]
            }
            """);

        var result = ToolCallValidation.ValidateAndNormalize(schema, Args("{}"));

        Assert.Equal(
            "Missing required parameter 'name'. Expected shape: {name: string, limit?: integer}.",
            result.Error);
    }

    [Fact]
    public void Skill_tool_synonyms_are_aliased()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" }, "path": { "type": "string" } },
              "required": ["name", "path"]
            }
            """);

        var result = ToolCallValidation.ValidateAndNormalize(
            schema, Args("""{ "skill": "release-notes", "file_path": "references/style.md" }"""));

        Assert.Null(result.Error);
        Assert.Equal(2, result.AppliedAliases.Count);
        Assert.Equal("release-notes", result.NormalizedArguments!["name"].GetString());
        Assert.Equal("references/style.md", result.NormalizedArguments["path"].GetString());
    }

    [Fact]
    public void Null_arguments_with_required_parameters_reports_missing()
    {
        var result = ToolCallValidation.ValidateAndNormalize(RouteSchema, arguments: null);

        Assert.StartsWith("Missing required parameters 'capability', 'tool', 'arguments'.", result.Error);
    }

    [Fact]
    public void Schema_without_properties_validates_nothing()
    {
        var result = ToolCallValidation.ValidateAndNormalize(
            Json("""{ "type": "object" }"""), Args("""{ "anything": 1 }"""));

        Assert.Null(result.Error);
        Assert.Empty(result.AppliedAliases);
    }

    [Fact]
    public void Valid_call_passes_unchanged()
    {
        var result = ToolCallValidation.ValidateAndNormalize(
            RouteSchema,
            Args("""{ "capability": "jira", "tool": "get_issue", "arguments": { "issueKey": "PROJ-1" } }"""));

        Assert.Null(result.Error);
        Assert.Empty(result.AppliedAliases);
        Assert.Equal(3, result.NormalizedArguments!.Count);
    }
}

using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Profiling;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Model-facing JSON must stay valid JSON but must not pay for HTML-safe escaping: relayed JSON
/// text and non-English text were measured at 1.4–2.6× their tokens with the default encoder.
/// </summary>
public sealed class ModelFacingJsonTests
{
    private const string Relayed = "[{\"orderId\":\"ORD-00050\",\"status\":\"ERR\"}]";
    private const string Swedish = "Plockning misslyckades: lagerplatsen är blockerad för påfyllnad.";

    [Fact]
    public void Quotes_in_relayed_text_are_backslash_escaped_not_unicode_escaped()
    {
        var json = OrchestratorJson.Serialize(new RouteView { Capability = "c", Tool = "t", Text = Relayed });

        Assert.DoesNotContain("\\u0022", json);
        Assert.Contains("\\\"orderId\\\"", json);
    }

    [Fact]
    public void Non_ascii_and_apostrophes_are_written_as_is()
    {
        var json = OrchestratorJson.Serialize(new ErrorView($"Use 'get_issue' — {Swedish}"));

        Assert.Contains(Swedish, json);
        Assert.Contains("'get_issue'", json);
        Assert.DoesNotContain("\\u00E4", json);
    }

    [Fact]
    public void Output_still_round_trips_as_valid_json()
    {
        var text = Relayed + "\n\t" + Swedish + " \\ <tag> & + `";
        var json = OrchestratorJson.Serialize(new RouteView { Capability = "c", Tool = "t", Text = text });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(text, doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Relayed_json_costs_far_fewer_tokens_than_with_the_default_encoder()
    {
        var counter = new Cl100kTokenCounter();
        var view = new RouteView { Capability = "c", Tool = "t", Text = Relayed };

        var now = counter.Count(OrchestratorJson.Serialize(view));
        var before = counter.Count(JsonSerializer.Serialize(view, OrchestratorJsonContext.Default.RouteView));

        Assert.True(now < before, $"expected fewer tokens than the default encoder ({now} vs {before})");
    }
}

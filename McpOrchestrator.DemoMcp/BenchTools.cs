using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpOrchestrator.DemoMcp;

/// <summary>
/// Stand-in for a "big result" downstream (think astro-schema's db_execute_query returning
/// 150 rows). Deterministic data so token benchmarks are repeatable. Selected by the
/// <c>bench</c> persona; used only by the round-trip token benchmarks.
/// </summary>
[McpServerToolType]
public sealed class BenchTools
{
    /// <summary>Every 50th row is in error, so 150 rows yield exactly 3 error rows.</summary>
    private const int ErrorEvery = 50;

    /// <summary>Returns <paramref name="count"/> warehouse order rows (default 150).</summary>
    [McpServerTool(Name = "query_orders")]
    [Description("Query warehouse order rows. Returns id, orderId, status, location, quantity, updatedBy and message per row.")]
    public static IReadOnlyList<OrderRow> QueryOrders(
        [Description("How many rows to return (default 150).")] int count = 150)
        => Enumerable.Range(1, Math.Clamp(count, 0, 5000)).Select(Row).ToList();

    /// <summary>Returns the detail record for one order id, e.g. 'ORD-00050'.</summary>
    [McpServerTool(Name = "get_order")]
    [Description("Get the detail record for one order by its orderId, e.g. 'ORD-00050'.")]
    public static OrderDetail GetOrder(
        [Description("The order id, e.g. 'ORD-00050'.")] string orderId)
    {
        var n = int.TryParse((orderId ?? string.Empty).Replace("ORD-", string.Empty), out var parsed) ? parsed : 0;
        var row = Row(Math.Max(n, 1));
        return new OrderDetail(
            row.OrderId, row.Status, row.Location, row.Quantity,
            Carrier: n % 2 == 0 ? "DHL" : "PostNord",
            LastEvent: row.Status == "ERR" ? "Pick failed: location blocked" : "Picked",
            History: new[] { "Created", "Released", row.Status == "ERR" ? "PickFailed" : "Picked" });
    }

    private static OrderRow Row(int i) => new(
        Id: i,
        OrderId: $"ORD-{i:D5}",
        Status: i % ErrorEvery == 0 ? "ERR" : "OK",
        Location: $"A{i % 12:D2}-{i % 7:D2}-{i % 4}",
        Quantity: (i * 7) % 40 + 1,
        UpdatedBy: i % 3 == 0 ? "wms-batch" : "operator",
        Message: i % ErrorEvery == 0 ? "Location blocked during pick" : "Picked and confirmed");
}

/// <summary>One row of the simulated order table.</summary>
public sealed record OrderRow(
    int Id, string OrderId, string Status, string Location, int Quantity, string UpdatedBy, string Message);

/// <summary>The detail record for one simulated order.</summary>
public sealed record OrderDetail(
    string OrderId, string Status, string Location, int Quantity, string Carrier, string LastEvent,
    IReadOnlyList<string> History);

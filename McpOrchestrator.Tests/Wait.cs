namespace McpOrchestrator.Tests;

/// <summary>
/// Polling helper for timing-dependent asserts (file watchers, debounce, background reloads):
/// retry the assertion until it passes or the timeout elapses — never a bare sleep-then-assert.
/// </summary>
internal static class Wait
{
    public static async Task ForAssertionAsync(Action assertion, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }
}

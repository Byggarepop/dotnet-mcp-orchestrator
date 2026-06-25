using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Groups the test classes that capture command output by swapping the process-global
/// <see cref="System.Console"/> writers (<c>Console.SetOut</c>/<c>Console.SetError</c>).
/// Because that redirection is global, two such classes running in parallel can steal each
/// other's output — one calling <c>Console.SetError</c> mid-run leaves the other's captured
/// stderr empty. <see cref="CollectionDefinitionAttribute"/> with
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> serializes every class in
/// this collection against the rest of the suite, so the global Console is only ever redirected
/// by one test at a time. (Observed as a flaky macOS-only failure in
/// <c>ProfileCliTests.Missing_config_file_fails_loudly</c>.)
/// </summary>
[CollectionDefinition("ConsoleSerial", DisableParallelization = true)]
public sealed class ConsoleSerialCollection;

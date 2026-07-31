using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// One configured origin of skills. <see cref="LoadAsync"/> produces fresh snapshots of every
/// skill the source currently holds; it must never throw — a broken source logs and returns
/// what it can (possibly nothing), so one bad origin cannot take down the others.
/// </summary>
internal interface ISkillSource
{
    /// <summary>The source's config id (audit logs, cache folders).</summary>
    string Id { get; }

    /// <summary>Loads the source's current skills.</summary>
    Task<IReadOnlyList<SkillSnapshot>> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>A skill source rooted at a local directory, scanned recursively.</summary>
internal sealed class DirectorySkillSource : ISkillSource
{
    private readonly string _path;
    private readonly ILogger _logger;

    internal DirectorySkillSource(string id, string path, ILogger logger)
    {
        Id = id;
        _path = path;
        _logger = logger;
    }

    public string Id { get; }

    /// <summary>The scanned root, watched by <see cref="SkillsReloadService"/> for live edits.</summary>
    internal string Path => _path;

    public Task<IReadOnlyList<SkillSnapshot>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(SkillDirectoryScanner.Scan(_path, Id, _logger));
}

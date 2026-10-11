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

    /// <summary>Loads the source's current skills, reporting where the content came from.</summary>
    Task<SkillSourceLoad> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Where the skills returned by one <see cref="ISkillSource.LoadAsync"/> call came from.</summary>
internal enum SkillSourceFreshness
{
    /// <summary>Read from the live origin this pass (a directory scan, a successful clone/fetch, an HTTP index fetch).</summary>
    Fresh,

    /// <summary>The origin could not be reached; an earlier successful copy was served instead.</summary>
    Cached,

    /// <summary>The origin could not be reached and no earlier copy exists; the source yielded nothing.</summary>
    Unavailable,
}

/// <summary>
/// The outcome of one source load: the skills plus how current they are. Same skill count can
/// mean a fresh download or a stale cache; the freshness tells the two apart in the log.
/// </summary>
internal sealed record SkillSourceLoad(IReadOnlyList<SkillSnapshot> Skills, SkillSourceFreshness Freshness)
{
    public static SkillSourceLoad Fresh(IReadOnlyList<SkillSnapshot> skills) => new(skills, SkillSourceFreshness.Fresh);

    public static SkillSourceLoad Cached(IReadOnlyList<SkillSnapshot> skills) => new(skills, SkillSourceFreshness.Cached);

    public static SkillSourceLoad Unavailable() => new([], SkillSourceFreshness.Unavailable);
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

    public Task<SkillSourceLoad> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(SkillSourceLoad.Fresh(SkillDirectoryScanner.Scan(_path, Id, _logger)));
}

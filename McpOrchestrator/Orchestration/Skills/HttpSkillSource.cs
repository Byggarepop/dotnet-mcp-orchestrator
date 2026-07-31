using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// A skill source served over HTTP(S): a discovery index (Agent Skills discovery format, see
/// <see cref="SkillIndexDocument"/>) lists each skill's SKILL.md URL and optionally its
/// supporting files; everything is fetched into snapshots. Plain HTTP has no recursive
/// directory listing, which is why an index is required rather than a bare base URL.
/// </summary>
internal sealed class HttpSkillSource : ISkillSource, IDisposable
{
    /// <summary>Cap on any single fetched payload, matching <see cref="SkillDirectoryScanner.MaxFileBytes"/>.</summary>
    private const long MaxResponseBytes = SkillDirectoryScanner.MaxFileBytes;

    private readonly Uri _indexUrl;
    private readonly string? _authorization;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    internal HttpSkillSource(string id, Uri indexUrl, string? authorization, ILogger logger, HttpMessageHandler? handler = null)
    {
        Id = id;
        _indexUrl = indexUrl;
        _authorization = authorization;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Id { get; }

    public async Task<IReadOnlyList<SkillSnapshot>> LoadAsync(CancellationToken cancellationToken)
    {
        SkillIndexDocument? index;
        try
        {
            var json = await GetStringAsync(_indexUrl, cancellationToken);
            index = JsonSerializer.Deserialize(json, SkillIndexJsonContext.Default.SkillIndexDocument);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            _logger.LogWarning("skill source {Source}: failed to fetch or parse index {Url}: {Message}", Id, _indexUrl, ex.Message);
            return [];
        }

        var skills = new List<SkillSnapshot>();
        foreach (var entry in index?.Skills ?? [])
        {
            if (entry.Type is not null && entry.Type != "skill-md")
            {
                continue; // Template or unknown entry kinds are not materializable.
            }

            if (string.IsNullOrEmpty(entry.Url))
            {
                _logger.LogWarning("skill source {Source}: index entry '{Name}' has no url; skipped", Id, entry.Name);
                continue;
            }

            var skill = await TryLoadEntryAsync(entry, cancellationToken);
            if (skill is not null)
            {
                skills.Add(skill);
            }
        }

        return skills;
    }

    private async Task<SkillSnapshot?> TryLoadEntryAsync(SkillIndexEntry entry, CancellationToken cancellationToken)
    {
        var skillMdUrl = new Uri(_indexUrl, entry.Url);
        string content;
        try
        {
            content = await GetStringAsync(skillMdUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            _logger.LogWarning("skill source {Source}: failed to fetch {Url}: {Message}; skill skipped", Id, skillMdUrl, ex.Message);
            return null;
        }

        if (!SkillFrontmatterParser.TryParse(content, out var frontmatter, out var error))
        {
            _logger.LogWarning("skill source {Source}: invalid skill at {Url}: {Error}; skipped", Id, skillMdUrl, error);
            return null;
        }

        var files = new List<SkillFile> { new(SkillSnapshot.SkillFileName, Encoding.UTF8.GetBytes(content)) };
        var folderUrl = new Uri(skillMdUrl, "."); // The SKILL.md's directory; file paths resolve under it.
        foreach (var relative in entry.Files ?? [])
        {
            if (!SkillPathValidator.TryNormalize(relative, out var normalized) ||
                normalized == SkillSnapshot.SkillFileName)
            {
                _logger.LogWarning("skill source {Source}: skill '{Name}' lists invalid file path '{Path}'; file skipped", Id, frontmatter.Name, relative);
                continue;
            }

            try
            {
                files.Add(new SkillFile(normalized, await GetBytesAsync(new Uri(folderUrl, normalized), cancellationToken)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                _logger.LogWarning("skill source {Source}: failed to fetch skill file {Path}: {Message}; file skipped", Id, normalized, ex.Message);
            }
        }

        return new SkillSnapshot(frontmatter.Name, frontmatter.Description, frontmatter.Body, Id, files);
    }

    private async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
        => Encoding.UTF8.GetString(await GetBytesAsync(url, cancellationToken));

    private async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException($"payload exceeds {MaxResponseBytes} bytes");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxResponseBytes)
        {
            throw new InvalidDataException($"payload exceeds {MaxResponseBytes} bytes");
        }

        return buffer.ToArray();
    }

    public void Dispose() => _http.Dispose();
}

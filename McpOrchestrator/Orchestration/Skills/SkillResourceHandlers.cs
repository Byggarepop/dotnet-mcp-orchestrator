using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Delivery mode B: skills as MCP Resources per SEP-2640 — every skill file at
/// <c>skill://&lt;name&gt;/&lt;path&gt;</c> plus the <c>skill://index.json</c> catalog. Handlers
/// resolve the live <see cref="SkillRegistry"/> per request, so a hot reload is visible to the
/// next <c>resources/list</c> without restart. All URI/format conventions live in
/// <see cref="Sep2640Conventions"/> (SEP-2640 is still a pending proposal).
/// </summary>
internal static class SkillResourceHandlers
{
    /// <summary>Handles <c>resources/list</c>: the index plus one resource per skill file.</summary>
    internal static ValueTask<ListResourcesResult> ListAsync(
        RequestContext<ListResourcesRequestParams> context, CancellationToken cancellationToken)
    {
        var (registry, delivery, _) = ResolveServices(context.Services);
        var result = new ListResourcesResult();
        if (!delivery.Resources)
        {
            return ValueTask.FromResult(result);
        }

        var catalog = registry.Current;
        if (catalog.Skills.Count > 0)
        {
            result.Resources.Add(new Resource
            {
                Uri = Sep2640Conventions.IndexUri,
                Name = "index.json",
                Description = "Catalog of the skills this server exposes (Agent Skills discovery format).",
                MimeType = "application/json",
            });
        }

        foreach (var skill in catalog.Skills)
        {
            foreach (var file in skill.Files)
            {
                var isSkillMd = file.RelativePath == SkillSnapshot.SkillFileName;
                result.Resources.Add(new Resource
                {
                    Uri = Sep2640Conventions.BuildUri(skill.Name, file.RelativePath),
                    // Per the SEP-2640 draft, the SKILL.md resource carries the skill's
                    // frontmatter name and description as its metadata.
                    Name = isSkillMd ? skill.Name : $"{skill.Name}/{file.RelativePath}",
                    Description = isSkillMd ? skill.Description : null,
                    MimeType = Sep2640Conventions.GetMimeType(file.RelativePath),
                });
            }
        }

        return ValueTask.FromResult(result);
    }

    /// <summary>Handles <c>resources/read</c> for <c>skill://</c> URIs.</summary>
    internal static ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var (registry, delivery, logger) = ResolveServices(context.Services);
        var uri = context.Params?.Uri ?? string.Empty;
        var catalog = registry.Current;

        if (!delivery.Resources)
        {
            throw new ModelContextProtocol.McpException($"unknown resource: {uri}");
        }

        if (uri == Sep2640Conventions.IndexUri)
        {
            return ValueTask.FromResult(new ReadResourceResult
            {
                Contents =
                {
                    new TextResourceContents
                    {
                        Uri = uri,
                        MimeType = "application/json",
                        Text = Sep2640Conventions.BuildIndexJson(catalog),
                    },
                },
            });
        }

        if (!Sep2640Conventions.TryParseUri(uri, out var skillName, out var relativePath)
            || !catalog.TryGet(skillName, out var skill)
            || !SkillPathValidator.TryNormalize(relativePath, out var normalized)
            || !skill.TryGetFile(normalized, out var file))
        {
            throw new ModelContextProtocol.McpException($"unknown resource: {uri}");
        }

        // The audit trail, same shape as the tool path but mode=resource.
        logger.LogInformation(
            "skill served: skill={Skill} file={File} hash={Hash} source={Source} mode=resource",
            skill.Name, normalized, skill.Sha256, skill.SourceId);

        var mimeType = Sep2640Conventions.GetMimeType(normalized);
        ResourceContents contents = Sep2640Conventions.IsTextMimeType(mimeType)
            ? new TextResourceContents
            {
                Uri = uri,
                MimeType = mimeType,
                Text = System.Text.Encoding.UTF8.GetString(file.Content),
            }
            : new BlobResourceContents
            {
                Uri = uri,
                MimeType = mimeType,
                Blob = file.Content,
            };

        return ValueTask.FromResult(new ReadResourceResult { Contents = { contents } });
    }

    private static (SkillRegistry Registry, SkillDeliveryOptions Delivery, ILogger Logger) ResolveServices(
        IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var reload = services.GetRequiredService<SkillsReloadService>();
        return (
            services.GetRequiredService<SkillRegistry>(),
            reload.Delivery,
            services.GetRequiredService<ILoggerFactory>().CreateLogger("McpOrchestrator.Skills"));
    }
}

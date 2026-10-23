using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Retroscope.Core.Adf;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jira;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

/// <summary>
/// A description as the rich editor loads it (UX10): <paramref name="Adf"/> is Jira's ADF with every part the editor
/// does not understand replaced by a token; <paramref name="Updated"/> is the <c>descriptionBase</c> of the save.
/// </summary>
/// <param name="Editable">False when Jira's description is not an ADF document; then <paramref name="Adf"/> is null.</param>
public sealed record JiraDescriptionEditView(
    string Key,
    string Updated,
    bool Editable,
    JsonElement? Adf,
    IReadOnlyList<JiraEditMediaView> Media,
    IReadOnlyList<JiraOpaqueView> Opaque,
    IReadOnlyList<JiraOpaqueMarkView> Marks,
    JiraEditLimitsView Limits);

/// <param name="Id">The media node's Media Services id.</param>
/// <param name="AttachmentId">The attachment the proxy serves for it; null when unresolved.</param>
public sealed record JiraEditMediaView(string Id, string? AttachmentId, string? Name, bool Image, double? W, double? H);

/// <param name="Preview">The original, rendered read-only.</param>
public sealed record JiraOpaqueView(string Ref, string Kind, bool Inline, string Label, Doc Preview);

public sealed record JiraOpaqueMarkView(string Ref, string Kind);

public sealed record JiraEditLimitsView(int Nodes, int Depth, int Text, int Bytes);

/// <summary>A route's own request body cap, below the server-wide one.</summary>
public sealed class RequestBodyLimit(long bytes) : IRequestSizeLimitMetadata {
    public long? MaxRequestBodySize => bytes;
}

public static partial class JiraEndpoints {
    /// <summary>The issue PATCH body cap: a whole rich description plus the other fields.</summary>
    public const long MaxPatchBytes = 1024 * 1024;

    private const string EmptyDoc = """{"type":"doc","version":1,"content":[]}""";

    private sealed record RichSave(byte[]? Adf);

    // Only editors receive the editable ADF (UX10 approval 4); viewers keep the Doc of GET issues/{key}.
    private static async Task<IResult> DescriptionAsync(HttpContext context, Guid teamId, string key, JiraGate gate, IslandStore islands) {
        var (call, refusal) = await GateIssueAsync(context, teamId, key, Capability.JiraEditIssues, gate);
        if (refusal is not null) {
            return refusal;
        }
        return await GuardAsync(async () => {
            // The editor starts from Jira's copy, not the drawer's cached one, so a save is not stale from the start.
            var detail = await islands.FreshAsync(call!.Team, key, context.RequestAborted);
            var view = AdfEditProjection.Project(detail.Issue.Fields?.Description, detail.Attachments);
            context.Response.Headers.CacheControl = "no-store";
            return Ok(EditView(detail, view), JiraHttpJsonContext.Default.JiraDescriptionEditView);
        });
    }

    // Shape checks before any Jira call; the rules that need the base run in WriteRichAsync.
    private static RichSave? RichDescription(JiraIssuePatch body, Dictionary<string, string> fields) {
        var adf = body.DescriptionAdf;
        if (adf.ValueKind == JsonValueKind.Undefined) {
            return null;
        }
        if (Blank(body.DescriptionBase)) {
            fields["descriptionBase"] = "required";
        }
        if (body.Description is not null) {
            fields["descriptionAdf"] = "conflict";
            return null;
        }
        if (adf.ValueKind == JsonValueKind.Null) {
            return new RichSave(null);
        }
        if (adf.ValueKind != JsonValueKind.Object || !adf.TryGetProperty("type", out var type) || !type.ValueEquals("doc")) {
            fields["descriptionAdf"] = AdfWriteCodes.NotDoc;
            return null;
        }
        return new RichSave(Encoding.UTF8.GetBytes(adf.GetRawText()));
    }

    /// <summary>The ADF to send Jira, or the refusal: stale when Jira's copy moved on or holds no token the save
    /// names, invalid with the first error's code and path otherwise.</summary>
    private static async Task<(JsonElement? Adf, IResult? Refused)> WriteRichAsync(
        TeamId team,
        string key,
        string descriptionBase,
        RichSave save,
        IslandStore islands,
        CancellationToken cancellationToken) {
        var detail = await islands.CurrentAsync(team, key, descriptionBase, cancellationToken);
        var view = detail is null ? null : AdfEditProjection.Project(detail.Issue.Fields?.Description, detail.Attachments);
        if (view is not { Editable: true }) {
            return (null, await StaleAsync(team, key, islands, cancellationToken));
        }
        if (save.Adf is null) {
            return (Parse(EmptyDoc), null);
        }
        if (AdfWrite.TryWrite(save.Adf, new AdfWriteContext(view), out var written, out var errors, out var missing)) {
            using var document = JsonDocument.Parse(written);
            return (document.RootElement.Clone(), null);
        }
        if (missing.Count > 0) {
            return (null, await StaleAsync(team, key, islands, cancellationToken));
        }
        return (null, Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> {
            ["descriptionAdf"] = errors[0].Code,
            ["descriptionAdf.at"] = errors[0].Path,
        }));
    }

    private static JiraDescriptionEditView EditView(IssueDetail detail, AdfEditView view) {
        JsonElement? adf = null;
        if (view.Editable) {
            using var document = JsonDocument.Parse(view.Adf);
            adf = document.RootElement.Clone();
        }
        return new JiraDescriptionEditView(
            detail.Issue.Key ?? "",
            detail.Updated,
            view.Editable,
            adf,
            [.. view.Media.Select(m => new JiraEditMediaView(m.Id, m.AttachmentId, m.Name, m.Image, m.Width, m.Height))],
            [.. view.Opaque.Select(part => OpaqueView(part, detail.Attachments))],
            [.. view.Marks.Select(mark => new JiraOpaqueMarkView(mark.Ref, mark.Kind))],
            new JiraEditLimitsView(view.Limits.Nodes, view.Limits.Depth, view.Limits.Text, view.Limits.Bytes));
    }

    // An extension names itself by its fallback text or key; anything else by its type.
    private static JiraOpaqueView OpaqueView(AdfOpaquePart part, IReadOnlyList<AdfAttachment> attachments) {
        using var document = JsonDocument.Parse(part.Raw);
        var original = document.RootElement;
        var label = Attr(original, "text") ?? Attr(original, "extensionKey") ?? part.Kind;
        return new JiraOpaqueView(part.Ref, part.Kind, part.Inline, label, AdfToDoc.Render(original, attachments));
    }

    private static string? Attr(JsonElement node, string name) {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Object
            || !attrs.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) {
            return null;
        }
        try {
            return value.GetString() is { Length: > 0 and <= 200 } text ? text : null;
        } catch (InvalidOperationException) {
            return null;
        }
    }
}

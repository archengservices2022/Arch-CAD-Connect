using System.Text.Json.Serialization;

namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// The client-side view of the server's Managed Workspace Plan contract
/// (<c>arch-plm.cad-workspace-plan</c>, P3B-2A) returned by
/// <c>GET /api/cad-documents/:id/workspace-plan</c>.
///
/// This is a faithful mirror of <c>web/app/lib/workspace-plan-core.ts</c> -
/// property names match the server JSON exactly. It is DATA ONLY; validation
/// and the "is this contract usable" decision live in
/// <see cref="WorkspacePlanContract"/> and the API layer's plan client.
///
/// IDENTITY: <see cref="WorkspacePlanEntry.CadDocumentId"/> and
/// <see cref="WorkspacePlanVersion.FileVersionId"/> are the ONLY authoritative
/// identities. <c>DocumentNumber</c>, <c>FileName</c> and the generated
/// placement path are display-only and are NEVER used as a lookup key or
/// treated as identity.
/// </summary>
public sealed record WorkspacePlan
{
    [JsonPropertyName("contract")] public WorkspacePlanContractRef Contract { get; init; } = new();
    [JsonPropertyName("root")] public WorkspacePlanRoot Root { get; init; } = new();
    [JsonPropertyName("generatedAt")] public string GeneratedAt { get; init; } = "";
    [JsonPropertyName("documentCount")] public int DocumentCount { get; init; }

    /// <summary>
    /// True only when every required document resolved to a real stored
    /// binary FileVersion, with no path collisions and no dangling
    /// dependencies. A plan that is not <c>safe</c> MUST NOT be materialized.
    /// </summary>
    [JsonPropertyName("safe")] public bool Safe { get; init; }

    [JsonPropertyName("entries")] public IReadOnlyList<WorkspacePlanEntry> Entries { get; init; } = [];
    [JsonPropertyName("problems")] public IReadOnlyList<WorkspacePlanProblem> Problems { get; init; } = [];
    [JsonPropertyName("traversal")] public WorkspacePlanTraversal Traversal { get; init; } = new();
}

public sealed record WorkspacePlanContractRef
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
}

public sealed record WorkspacePlanRoot
{
    [JsonPropertyName("cadDocumentId")] public string CadDocumentId { get; init; } = "";
}

public sealed record WorkspacePlanTraversal
{
    [JsonPropertyName("visitedCount")] public int VisitedCount { get; init; }
    [JsonPropertyName("edgeCount")] public int EdgeCount { get; init; }
    [JsonPropertyName("maxNodes")] public int MaxNodes { get; init; }
}

public sealed record WorkspacePlanEntry
{
    /// <summary>Authoritative identity.</summary>
    [JsonPropertyName("cadDocumentId")] public string CadDocumentId { get; init; } = "";

    /// <summary>Display only - never identity.</summary>
    [JsonPropertyName("documentNumber")] public string DocumentNumber { get; init; } = "";

    /// <summary>Display only - never identity.</summary>
    [JsonPropertyName("fileName")] public string FileName { get; init; } = "";

    [JsonPropertyName("cadType")] public string CadType { get; init; } = "";
    [JsonPropertyName("isRoot")] public bool IsRoot { get; init; }
    [JsonPropertyName("placement")] public WorkspacePlanPlacement Placement { get; init; } = new();

    /// <summary>Outgoing parent -> child edges to other entries IN this plan.</summary>
    [JsonPropertyName("dependsOn")] public IReadOnlyList<WorkspacePlanDependency> DependsOn { get; init; } = [];

    /// <summary>True iff this entry pins a real, safely-placeable FileVersion.</summary>
    [JsonPropertyName("canMaterialize")] public bool CanMaterialize { get; init; }

    [JsonPropertyName("blockedReasons")] public IReadOnlyList<string> BlockedReasons { get; init; } = [];

    /// <summary>Present iff <see cref="CanMaterialize"/>; null otherwise.</summary>
    [JsonPropertyName("version")] public WorkspacePlanVersion? Version { get; init; }
}

public sealed record WorkspacePlanPlacement
{
    /// <summary>
    /// Server-generated, flat, safe relative path. NOT an Inventor
    /// project-relative reference path. Never identity. Re-validated
    /// client-side before any filesystem write (see
    /// <see cref="SafeWorkspacePath"/>).
    /// </summary>
    [JsonPropertyName("generatedRelativePath")] public string GeneratedRelativePath { get; init; } = "";
}

public sealed record WorkspacePlanDependency
{
    [JsonPropertyName("cadDocumentId")] public string CadDocumentId { get; init; } = "";
    [JsonPropertyName("dependencyType")] public string DependencyType { get; init; } = "";
}

public sealed record WorkspacePlanVersion
{
    /// <summary>Authoritative identity of the exact stored binary this entry pins.</summary>
    [JsonPropertyName("fileVersionId")] public string FileVersionId { get; init; } = "";

    [JsonPropertyName("versionNumber")] public int VersionNumber { get; init; }

    /// <summary>Lowercase hex SHA-256 the downloaded bytes MUST match.</summary>
    [JsonPropertyName("checksum")] public string Checksum { get; init; } = "";

    /// <summary>Exact byte size the download MUST match.</summary>
    [JsonPropertyName("fileSize")] public long FileSize { get; init; }

    /// <summary>Display only - never identity.</summary>
    [JsonPropertyName("originalFileName")] public string OriginalFileName { get; init; } = "";

    /// <summary>
    /// The relative URL of the authenticated download endpoint. The client
    /// only ever fetches this when it is EXACTLY
    /// <c>/api/file-versions/&lt;FileVersionId&gt;/content</c>.
    /// </summary>
    [JsonPropertyName("contentPath")] public string ContentPath { get; init; } = "";
}

public sealed record WorkspacePlanProblem
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("cadDocumentId")] public string? CadDocumentId { get; init; }
    [JsonPropertyName("cadDocumentIds")] public IReadOnlyList<string>? CadDocumentIds { get; init; }
    [JsonPropertyName("workspacePath")] public string? WorkspacePath { get; init; }
}

/// <summary>
/// The one contract this client supports, and the "is this plan usable"
/// decision - the C# equivalent of the materializer's
/// <c>isSupportedWorkspaceContractVersion</c> + the plan-client's shape check.
/// </summary>
public static class WorkspacePlanContract
{
    public const string Id = "arch-plm.cad-workspace-plan";
    public const string Version = "1.0.0";

    private static readonly int SupportedMajor = int.Parse(Version.Split('.')[0]);

    public static bool IsSupportedVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var head = version.Split('.')[0];
        return int.TryParse(head, out var major) && major == SupportedMajor;
    }

    /// <summary>Basic structural sanity of a deserialized plan (the transport
    ///  layer has already checked HTTP status / redirects).</summary>
    public static bool HasValidShape(WorkspacePlan? plan) =>
        plan is not null
        && !string.IsNullOrEmpty(plan.Contract.Id)
        && !string.IsNullOrEmpty(plan.Contract.Version)
        && !string.IsNullOrEmpty(plan.Root.CadDocumentId)
        && plan.Entries is not null;
}

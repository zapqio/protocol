using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zapqio.Deployments;

// Deployment messages in the shared runner protocol. Compiled into both protocol assemblies.
public static class DeploymentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

public sealed record ExecutionVersion(Guid RepositoryId, Guid SnapshotId, string Commit)
{
    public Guid? DeploymentId { get; init; }
}

public sealed record BundleFile(string Path, long Size, string Sha256);

public sealed record DeploymentManifest
{
    public int FormatVersion { get; init; } = 1;
    public Guid DeploymentId { get; init; }
    public Guid RepositoryId { get; init; }
    public Guid SnapshotId { get; init; }
    public long Sequence { get; init; }
    public string RepositoryName { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string Commit { get; init; } = "";
    public string Author { get; init; } = "";
    public string Message { get; init; } = "";
    public string Action { get; init; } = "Apply";
    public string ContentKind { get; init; } = "DotnetModuleZip";
    public string PayloadSha256 { get; init; } = "";
    public long PayloadBytes { get; init; }
    public List<BundleFile> Files { get; init; } = [];
}

public sealed record DeploymentNotice
{
    public Guid DeploymentId { get; init; }
    public Guid RepositoryId { get; init; }
    public long Sequence { get; init; }
    public string Status { get; init; } = "Pending";
    public string? BundleUrl { get; init; }
    public string? BundleSha256 { get; init; }
    public long BundleBytes { get; init; }
    public long MaxBytes { get; init; }
    public int MaxFiles { get; init; }
}

public sealed record PackageApproval(Guid DeploymentId, string BundleSha256);
public sealed record DeploymentApproval(Guid BatchId, List<PackageApproval> Packages);
public sealed record DeploymentApprovalResult(Guid BatchId, bool Accepted, string? Reason);
public sealed record DeploymentStatusItem(Guid DeploymentId, long Revision, string Status, string? Reason);
public sealed record DeploymentReport(Guid? BatchId, List<DeploymentStatusItem> Items);
public sealed record DeploymentReceipt(Guid DeploymentId, long Revision);
public sealed record DeploymentStatusAck(List<DeploymentReceipt> Items);

using System;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Buildings;

/// <summary>
/// Proposed storage aggregate for an operator's membership in ONE building.
/// RFC/SQLite test design only: NOT mapped in the running module DbContext.
/// The caller must authorize grant/revoke separately using real ABP identity,
/// a privileged administrative capability and server-resolved building scope.
/// </summary>
public sealed class BuildingOperatorAccessGrant : AggregateRoot<Guid>
{
    public Guid OperatorUserId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid GrantedByUserId { get; private set; }
    public DateTime GrantedAtUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public Guid? RevokedByUserId { get; private set; }
    public int RevisionNumber { get; private set; }

    private BuildingOperatorAccessGrant() { }

    private BuildingOperatorAccessGrant(Guid id) : base(id) { }

    public static BuildingOperatorAccessGrant Issue(
        Guid id,
        Guid operatorUserId,
        Guid buildingId,
        Guid grantedByUserId,
        DateTime grantedAtUtc,
        DateTime? expiresAtUtc)
    {
        RequireId(id, nameof(id));
        RequireId(operatorUserId, nameof(operatorUserId));
        RequireId(buildingId, nameof(buildingId));
        RequireId(grantedByUserId, nameof(grantedByUserId));
        RequireUtc(grantedAtUtc, nameof(grantedAtUtc));
        if (expiresAtUtc.HasValue)
        {
            RequireUtc(expiresAtUtc.Value, nameof(expiresAtUtc));
            if (expiresAtUtc.Value <= grantedAtUtc)
                throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        }

        return new BuildingOperatorAccessGrant(id)
        {
            OperatorUserId = operatorUserId,
            BuildingId = buildingId,
            GrantedByUserId = grantedByUserId,
            GrantedAtUtc = grantedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            RevisionNumber = 1
        };
    }

    /// <summary>
    /// A revoked row stays revoked; a regrant is a NEW row and historical
    /// evidence is retained. The DB partial uniqueness index prevents two
    /// simultaneously unrevoked grants for the same user/building.
    /// </summary>
    public void Revoke(Guid revokedByUserId, DateTime revokedAtUtc)
    {
        RequireId(revokedByUserId, nameof(revokedByUserId));
        RequireUtc(revokedAtUtc, nameof(revokedAtUtc));
        if (RevokedAtUtc.HasValue)
            throw new InvalidOperationException("Membership already revoked.");
        if (revokedAtUtc < GrantedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(revokedAtUtc));

        RevokedAtUtc = revokedAtUtc;
        RevokedByUserId = revokedByUserId;
        checked { RevisionNumber++; }
    }

    /// <summary>
    /// Validity is a necessary condition ONLY. Caller still must verify the
    /// authenticated account is enabled, permission is granted, and the
    /// building is active; a stale preview may never authorize a write.
    /// </summary>
    public bool IsEffectiveAt(DateTime nowUtc)
    {
        RequireUtc(nowUtc, nameof(nowUtc));
        return nowUtc >= GrantedAtUtc &&
               (!RevokedAtUtc.HasValue || nowUtc < RevokedAtUtc.Value) &&
               (!ExpiresAtUtc.HasValue || nowUtc < ExpiresAtUtc.Value);
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A nonempty identifier is required.", name);
    }

    private static void RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Timestamp must be UTC.", name);
    }
}

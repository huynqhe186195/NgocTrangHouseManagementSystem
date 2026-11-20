using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.OperatorMemberships;

public enum OperatorMembershipStatus
{
    Active = 1,
    Expired = 2,
    Revoked = 3,
    Scheduled = 4
}

/// <summary>
/// Narrow, read-only membership evidence. Identity profile data is deliberately
/// not included until a separately reviewed, batched resolver exists.
/// </summary>
public class OperatorMembershipSummaryDto : EntityDto<Guid>
{
    public Guid OperatorUserId { get; set; }
    public Guid BuildingId { get; set; }
    public string BuildingName { get; set; } = default!;
    public Guid GrantedByUserId { get; set; }
    public DateTime GrantedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public int RevisionNumber { get; set; }
    public OperatorMembershipStatus Status { get; set; }
}

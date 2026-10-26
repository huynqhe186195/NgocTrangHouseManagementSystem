using System;
using System.Collections.Generic;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// Server-generated, internal-only preflight evidence. Blockers are facts
/// about unfinished work, NOT a license to end the contract or post money.
/// </summary>
public sealed class MoveOutCompletionReadinessDto
{
    public Guid MoveOutRequestId { get; set; }
    public Guid ContractId { get; set; }
    public Guid RoomId { get; set; }
    public MoveOutRequestStatus RequestStatus { get; set; }
    public bool HasFinalMeterEvidence { get; set; }
    public int ChecklistTotal { get; set; }
    public int ChecklistCompleted { get; set; }
    public bool HasUtilityDraft { get; set; }
    public List<string> Blockers { get; set; } = new();

    // Phase 2G.3 / 2G.4 have not enabled issued invoice settlement,
    // debt closeout or audited deposit payout. The readiness API MUST
    // NEVER return approval until these are verified in one transaction.
    public bool CanCompleteMoveOut => false;
}

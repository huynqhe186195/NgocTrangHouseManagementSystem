using System.Threading.Tasks;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;

namespace BuildingManagement.Contracts;

/// <summary>
/// A direct contract-end command cannot prove final meters, an issued and
/// settled final invoice, approved deposit disposition or signed handover.
/// It MUST NEVER complete a move-out by itself.
/// </summary>
public interface IDirectContractTerminationGate
{
    Task EnsureDirectTerminationAllowedAsync();
}

/// <summary>
/// Explicit default deny. A future CompleteMoveOut orchestration must perform
/// the termination in its own verified atomic transaction, NOT by granting
/// blanket access to the historic EndAsync method.
/// </summary>
[ExposeServices(typeof(IDirectContractTerminationGate))]
public sealed class DenyAllDirectContractTerminationGate :
    IDirectContractTerminationGate, ITransientDependency
{
    public Task EnsureDirectTerminationAllowedAsync()
        => throw new AbpAuthorizationException(
            "Direct contract termination is disabled. Use the approved " +
            "CompleteMoveOut process once final invoice, debt, deposit " +
            "and handover validations are implemented.");
}

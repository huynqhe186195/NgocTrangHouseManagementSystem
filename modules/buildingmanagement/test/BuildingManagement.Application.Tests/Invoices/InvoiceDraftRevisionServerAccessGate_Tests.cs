using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace BuildingManagement.Invoices;

/// <summary>
/// Server access composition tests using faked ABP principal, DB resource
/// and server building grant providers. No real user grant, SQL, migration
/// or invoice mutation occurs. The production default remains DENY ALL.
/// </summary>
public sealed class InvoiceDraftRevisionServerAccessGate_Tests
{
    [Fact]
    public async Task SERVERACCESS01_Production_Default_Grants_Deny_Even_With_Permission()
    {
        var env = Valid();
        var gate = new InvoiceDraftRevisionServerAccessGate(
            env.Principal, env.Resources,
            new DenyAllInvoiceDraftRevisionBuildingGrants());
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
    }

    [Fact]
    public async Task SERVERACCESS02_All_Server_Facts_Matching_Is_Design_Only()
    {
        var env = Valid();
        Assert.Equal(InvoiceDraftRevisionAccessStatus.EligibleForDesignOnly,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
    }

    [Fact]
    public async Task SERVERACCESS03_Anonymous_Actor_Denied_Without_Reading_Database()
    {
        var env = Valid();
        env.Principal.User = null;
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
        Assert.Equal(0, env.Resources.Calls);
        Assert.Equal(0, env.Grants.Calls);
    }

    [Fact]
    public async Task SERVERACCESS04_Missing_Permission_Denied_Before_Resource_Lookup()
    {
        var env = Valid();
        env.Principal.User = new(env.UserId, false);
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
        Assert.Equal(0, env.Resources.Calls);
        Assert.Equal(0, env.Grants.Calls);
    }

    [Fact]
    public async Task SERVERACCESS05_Unknown_Invoice_Denied_Without_Grant_Lookup()
    {
        var env = Valid();
        env.Resources.Graph = null;
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
        Assert.Equal(0, env.Grants.Calls);
    }

    [Fact]
    public async Task SERVERACCESS06_Forged_Invoice_Id_Must_Not_Authorize()
    {
        var env = Valid();
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(Guid.NewGuid(),
                InvoiceDraftRevisionCapability.RebuildDraft));
    }

    [Fact]
    public async Task SERVERACCESS07_Broken_Server_Contract_Link_Denied()
    {
        var env = Valid();
        env.Resources.Graph = env.Resources.Graph! with
        {
            InvoiceContractId = Guid.NewGuid()
        };
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
    }

    [Fact]
    public async Task SERVERACCESS08_Revoked_Or_Cross_Building_Grant_Denied()
    {
        var env = Valid();
        env.Grants.Granted = new[] { Guid.NewGuid() };
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
    }

    [Fact]
    public async Task SERVERACCESS09_NonDraft_Invoice_Denied()
    {
        var env = Valid();
        env.Resources.Graph = env.Resources.Graph! with
        {
            InvoiceStatus = (InvoiceStatus)99
        };
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
    }

    [Fact]
    public async Task SERVERACCESS10_Invalid_Operation_Denied_Without_Principal_Read()
    {
        var env = Valid();
        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                (InvoiceDraftRevisionCapability)999));
        Assert.Equal(0, env.Principal.Calls);
        Assert.Equal(0, env.Resources.Calls);
    }

    [Fact]
    public async Task SERVERACCESS11_History_Requires_Its_Own_Permission()
    {
        var env = Valid();
        env.Principal.OnlyRebuildGranted = true;

        Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.ReadRevisionHistory));
        Assert.Equal(0, env.Resources.Calls);

        Assert.Equal(InvoiceDraftRevisionAccessStatus.EligibleForDesignOnly,
            await env.Gate.AssessDesignOnlyAsync(env.InvoiceId,
                InvoiceDraftRevisionCapability.RebuildDraft));
    }

    private static TestContext Valid()
    {
        var invoice = Guid.NewGuid();
        var contract = Guid.NewGuid();
        var room = Guid.NewGuid();
        var floor = Guid.NewGuid();
        var building = Guid.NewGuid();
        var user = Guid.NewGuid();

        var principal = new FakePrincipal { User = new(user, true) };
        var resources = new FakeResources
        {
            Graph = new InvoiceDraftRevisionResourceGraph(
                invoice, contract, room,
                contract, room,
                room, floor, floor, building,
                building, InvoiceStatus.Draft)
        };
        var grants = new FakeGrants { Granted = new[] { building } };
        var gate = new InvoiceDraftRevisionServerAccessGate(
            principal, resources, grants);
        return new TestContext(
            user, invoice, principal, resources, grants, gate);
    }

    private sealed record TestContext(
        Guid UserId,
        Guid InvoiceId,
        FakePrincipal Principal,
        FakeResources Resources,
        FakeGrants Grants,
        InvoiceDraftRevisionServerAccessGate Gate);

    private sealed class FakePrincipal : IInvoiceDraftRevisionPrincipalReader
    {
        public InvoiceDraftRevisionPrincipal? User { get; set; }
        public bool OnlyRebuildGranted { get; set; }
        public int Calls { get; private set; }

        public Task<InvoiceDraftRevisionPrincipal?> ReadAsync(
            InvoiceDraftRevisionCapability capability)
        {
            Calls++;
            if (OnlyRebuildGranted &&
                capability == InvoiceDraftRevisionCapability.ReadRevisionHistory &&
                User is not null)
                return Task.FromResult<InvoiceDraftRevisionPrincipal?>(
                    User with { PermissionGranted = false });
            return Task.FromResult(User);
        }
    }

    private sealed class FakeResources : IInvoiceDraftRevisionResourceGraphReader
    {
        public InvoiceDraftRevisionResourceGraph? Graph { get; set; }
        public int Calls { get; private set; }

        public Task<InvoiceDraftRevisionResourceGraph?> ReadAsync(Guid invoiceId)
        {
            Calls++;
            return Task.FromResult(Graph);
        }
    }

    private sealed class FakeGrants : IInvoiceDraftRevisionBuildingGrants
    {
        public IReadOnlyCollection<Guid> Granted { get; set; } = Array.Empty<Guid>();
        public int Calls { get; private set; }

        public Task<IReadOnlyCollection<Guid>> FindGrantedBuildingIdsAsync(
            Guid operatorId)
        {
            Calls++;
            return Task.FromResult(Granted);
        }
    }
}

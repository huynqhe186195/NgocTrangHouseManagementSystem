using System;
using BuildingManagement.Buildings;
using Xunit;

namespace BuildingManagement.Security;

public sealed class BuildingOperatorAccessGrant_Tests
{
    private static readonly DateTime Start =
        new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MEMBER01_New_Grant_Records_Auditable_Identity_And_Revision()
    {
        var id = Guid.NewGuid();
        var user = Guid.NewGuid();
        var building = Guid.NewGuid();
        var issuer = Guid.NewGuid();

        var grant = BuildingOperatorAccessGrant.Issue(
            id, user, building, issuer, Start, Start.AddDays(30));

        Assert.Equal(id, grant.Id);
        Assert.Equal(user, grant.OperatorUserId);
        Assert.Equal(building, grant.BuildingId);
        Assert.Equal(issuer, grant.GrantedByUserId);
        Assert.Equal(Start, grant.GrantedAtUtc);
        Assert.Equal(Start.AddDays(30), grant.ExpiresAtUtc);
        Assert.Null(grant.RevokedAtUtc);
        Assert.Null(grant.RevokedByUserId);
        Assert.Equal(1, grant.RevisionNumber);
    }

    [Fact]
    public void MEMBER02_Grant_Is_Not_Active_Before_Start()
    {
        var grant = NewGrant();
        Assert.False(grant.IsEffectiveAt(Start.AddTicks(-1)));
        Assert.True(grant.IsEffectiveAt(Start));
    }

    [Fact]
    public void MEMBER03_Expiration_Boundary_Is_Exclusive()
    {
        var grant = NewGrant();
        Assert.True(grant.IsEffectiveAt(Start.AddDays(30).AddTicks(-1)));
        Assert.False(grant.IsEffectiveAt(Start.AddDays(30)));
        Assert.False(grant.IsEffectiveAt(Start.AddDays(31)));
    }

    [Fact]
    public void MEMBER04_Revocation_Is_Audited_And_Irreversible()
    {
        var grant = NewGrant();
        var revoker = Guid.NewGuid();
        grant.Revoke(revoker, Start.AddDays(2));

        Assert.Equal(revoker, grant.RevokedByUserId);
        Assert.Equal(Start.AddDays(2), grant.RevokedAtUtc);
        Assert.Equal(2, grant.RevisionNumber);
        Assert.False(grant.IsEffectiveAt(Start.AddDays(3)));

        Assert.Throws<InvalidOperationException>(() =>
            grant.Revoke(Guid.NewGuid(), Start.AddDays(4)));
        Assert.Equal(2, grant.RevisionNumber);
    }

    [Fact]
    public void MEMBER05_Empty_Record_User_Building_Or_Issuer_Denied()
    {
        var id = Guid.NewGuid();
        var user = Guid.NewGuid();
        var building = Guid.NewGuid();
        var issuer = Guid.NewGuid();

        Assert.ThrowsAny<ArgumentException>(() =>
            BuildingOperatorAccessGrant.Issue(
                Guid.Empty, user, building, issuer, Start, null));
        Assert.ThrowsAny<ArgumentException>(() =>
            BuildingOperatorAccessGrant.Issue(
                id, Guid.Empty, building, issuer, Start, null));
        Assert.ThrowsAny<ArgumentException>(() =>
            BuildingOperatorAccessGrant.Issue(
                id, user, Guid.Empty, issuer, Start, null));
        Assert.ThrowsAny<ArgumentException>(() =>
            BuildingOperatorAccessGrant.Issue(
                id, user, building, Guid.Empty, Start, null));
    }

    [Fact]
    public void MEMBER06_Timestamps_Require_Actual_Utc_Kind()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), DateTime.SpecifyKind(Start,
                    DateTimeKind.Unspecified), null));
        Assert.ThrowsAny<ArgumentException>(() =>
            BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Start, DateTime.SpecifyKind(
                    Start.AddDays(1), DateTimeKind.Local)));

        var grant = NewGrant();
        Assert.ThrowsAny<ArgumentException>(() =>
            grant.IsEffectiveAt(DateTime.SpecifyKind(Start,
                DateTimeKind.Unspecified)));
        Assert.ThrowsAny<ArgumentException>(() =>
            grant.Revoke(Guid.NewGuid(), DateTime.SpecifyKind(
                Start.AddDays(1), DateTimeKind.Unspecified)));
        Assert.Null(grant.RevokedAtUtc);
    }

    [Fact]
    public void MEMBER07_Expired_At_Or_Before_Grant_Time_Denied()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Start, Start));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Start, Start.AddTicks(-1)));
    }

    [Fact]
    public void MEMBER08_Revocation_Cannot_Predate_Issue()
    {
        var grant = NewGrant();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            grant.Revoke(Guid.NewGuid(), Start.AddTicks(-1)));
        Assert.Null(grant.RevokedAtUtc);
        Assert.Equal(1, grant.RevisionNumber);
    }

    [Fact]
    public void MEMBER09_Null_Expiry_Is_Explicitly_Allowed_But_Requires_Review()
    {
        var grant = BuildingOperatorAccessGrant.Issue(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Start, null);

        Assert.True(grant.IsEffectiveAt(Start.AddYears(5)));
        grant.Revoke(Guid.NewGuid(), Start.AddDays(1));
        Assert.False(grant.IsEffectiveAt(Start.AddYears(5)));
    }

    [Fact]
    public void MEMBER10_Revoking_With_Empty_Actor_Is_Denied()
    {
        var grant = NewGrant();
        Assert.ThrowsAny<ArgumentException>(() =>
            grant.Revoke(Guid.Empty, Start.AddDays(1)));
        Assert.Null(grant.RevokedByUserId);
        Assert.Equal(1, grant.RevisionNumber);
    }

    [Fact]
    public void MEMBER11_Regrant_Must_Use_New_History_Record()
    {
        var old = NewGrant();
        old.Revoke(Guid.NewGuid(), Start.AddDays(2));
        var newer = BuildingOperatorAccessGrant.Issue(
            Guid.NewGuid(), old.OperatorUserId, old.BuildingId,
            Guid.NewGuid(), Start.AddDays(3), null);

        Assert.NotEqual(old.Id, newer.Id);
        Assert.Equal(2, old.RevisionNumber);
        Assert.Equal(1, newer.RevisionNumber);
        Assert.False(old.IsEffectiveAt(Start.AddDays(4)));
        Assert.True(newer.IsEffectiveAt(Start.AddDays(4)));
    }

    [Fact]
    public void MEMBER12_Recorded_Revocation_Denies_Even_Before_Stamped_Time()
    {
        var grant = NewGrant();
        // A future revocation timestamp must never leave the grant active
        // once the revocation has been recorded in the database.
        grant.Revoke(Guid.NewGuid(), Start.AddDays(3));

        Assert.False(grant.IsEffectiveAt(Start.AddDays(1)));
        Assert.False(grant.IsEffectiveAt(Start.AddDays(3)));
    }

    private static BuildingOperatorAccessGrant NewGrant()
        => BuildingOperatorAccessGrant.Issue(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Start, Start.AddDays(30));
}

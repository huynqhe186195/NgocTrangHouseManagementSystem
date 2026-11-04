using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// These are real DOCX package and HTTP endpoint structure tests. The
/// authenticated cross-building Owner/Staff/revocation attack tests remain
/// separate integration acceptance, NOT proven by static reflection.
/// </summary>
public sealed class Phase2HOwnerContractDocxExport_Tests
{
    private static SliceBContractDocumentData Sample(
        string contractNo="NT-2026/301",
        string room="301",
        IReadOnlyList<string>? tenants=null)
        => new(
            Guid.NewGuid(),Guid.NewGuid(),contractNo,
            "Tòa Ngọc Trang","12 Phố Thử Nghiệm, Hà Nội",room,"Active",
            new DateTime(2026,11,15),new DateTime(2027,11,14),
            3_000_000m,4_000_000m,null,
            tenants ?? new[]{"Nguyễn Thị A","Trần Văn B"});

    private static (byte[] File,string Xml) Extract(
        SliceBContractDocumentData data)
    {
        var bytes=SliceBStagingContractDocumentDocx.Export(data);
        using var stream=new MemoryStream(bytes);
        using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
        Assert.Contains(zip.Entries,x=>x.FullName=="[Content_Types].xml");
        Assert.Contains(zip.Entries,x=>x.FullName=="_rels/.rels");
        var doc=Assert.Single(zip.Entries.Where(
            x=>x.FullName=="word/document.xml"));
        using var reader=new StreamReader(doc.Open());
        return (bytes,reader.ReadToEnd());
    }

    [Fact]
    public void TRFDOC01_Genuine_Docx_Zip_And_WordprocessingML_With_Vietnamese()
    {
        var (file,xml)=Extract(Sample());
        Assert.Equal((byte)'P',file[0]);
        Assert.Equal((byte)'K',file[1]);
        var parsed=XDocument.Parse(xml);
        XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var text=string.Join(" ",parsed.Descendants(w+"t").Select(x=>x.Value));
        Assert.Contains("BẢN TRÍCH XUẤT DỮ LIỆU HỢP ĐỒNG",text);
        Assert.Contains("Tòa Ngọc Trang",text);
        Assert.Contains("Nguyễn Thị A",text);
        Assert.Contains("Trần Văn B",text);
        Assert.Contains("CHƯA PHẢI HỢP ĐỒNG ĐÃ KÝ",text);
        Assert.Contains("KHÔNG XÁC NHẬN VIỆC NHẬN TIỀN",text);
        Assert.Contains("3.000.000",text);
        Assert.Contains("4.000.000",text);
    }

    [Fact]
    public void TRFDOC02_Untrusted_Contract_And_Tenant_Text_Is_Xml_Escaped()
    {
        var (_,xml)=Extract(Sample(
            contractNo:"<script>alert('&')</script>",
            room:"301<bad>&",
            tenants:new[]{"<img src=x onerror=evil()> & quoted"}));
        Assert.DoesNotContain("<script>",xml);
        Assert.DoesNotContain("<img ",xml);
        Assert.Contains("&lt;script&gt;",xml);
        Assert.Contains("&lt;img",xml);
        Assert.Contains("&amp;",xml);
        XDocument.Parse(xml);
    }

    [Fact]
    public void TRFDOC03_Invalid_Or_Missing_Contract_Source_Refuses_Export()
    {
        Assert.Throws<InvalidOperationException>(()=>
            SliceBStagingContractDocumentDocx.Export(
                Sample() with { ContractId=Guid.Empty }));
        Assert.Throws<InvalidOperationException>(()=>
            SliceBStagingContractDocumentDocx.Export(
                Sample() with { MonthlyRentVnd=0m }));
        Assert.Throws<InvalidOperationException>(()=>
            SliceBStagingContractDocumentDocx.Export(
                Sample() with { BuildingAddress="" }));
        Assert.Throws<InvalidOperationException>(()=>
            SliceBStagingContractDocumentDocx.Export(
                Sample() with { TenantNames=new[]{""} }));
    }

    [Fact]
    public void TRFDOC04_File_Is_Derived_From_Original_Room_And_Terms()
    {
        var data=Sample(room:"902") with {
            MonthlyRentVnd=5_500_000m,
            RequiredDepositVnd=3_000_000m
        };
        var (_,xml)=Extract(data);
        Assert.Contains("902",xml);
        Assert.Contains("5.500.000",xml);
        Assert.Contains("3.000.000",xml);
        Assert.Contains(data.ContractId.ToString("D"),xml);
        Assert.DoesNotContain("ĐÃ THANH TOÁN",xml);
    }

    [Fact]
    public void TRFDOC05_Route_Requires_Authorization_And_Transactional_Uow()
    {
        var controller=typeof(SliceBStagingContractsController);
        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
        var method=controller.GetMethod("ExportOwnerDocumentAsync");
        Assert.NotNull(method);
        var route=Assert.Single(method!.GetCustomAttributes<HttpGetAttribute>());
        Assert.Equal("{id:guid}/export.docx",route.Template);
        var uow=Assert.Single(method.GetCustomAttributes<UnitOfWorkAttribute>());
        Assert.NotNull(uow);
        Assert.DoesNotContain(
            controller.GetMethods(BindingFlags.Public|BindingFlags.Instance|
                BindingFlags.DeclaredOnly)
                .Where(x=>x.Name=="ExportOwnerDocumentAsync"),
            x=>x.GetCustomAttribute<HttpPostAttribute>() is not null);
    }

    [Fact]
    public void TRFDOC06_Docx_Contains_No_External_Links_Or_Embedded_Pii()
    {
        var (file,xml)=Extract(Sample());
        using var zip=new ZipArchive(new MemoryStream(file),ZipArchiveMode.Read);
        Assert.DoesNotContain(zip.Entries,
            entry=>entry.FullName.StartsWith("word/media/",StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries,
            entry=>entry.FullName.Contains("external",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("CCCD",xml);
        Assert.DoesNotContain("Số tài khoản",xml);
    }
}

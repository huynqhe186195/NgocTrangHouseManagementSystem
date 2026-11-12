using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Authentic, CURRENT contract-data extract; not a signed contract and
/// not a substitute for wet-ink documents, reviewed legal clauses, an
/// e-signature, a new contract mutation or a deposit/cash receipt.
/// Only the authenticated, owner-scoped adapter may construct this model.
/// No personally identifying numbers, phone or payment account exported.
/// </summary>
public sealed record SliceBContractDocumentData(
    Guid ContractId, Guid BuildingId, string ContractNumber,
    string BuildingName, string BuildingAddress, string RoomNumber,
    string ContractStatus, DateTime StartDate, DateTime? EndDate,
    decimal MonthlyRentVnd, decimal RequiredDepositVnd,
    Guid? RenewedFromContractId, IReadOnlyList<string> TenantNames);

public static class SliceBStagingContractDocumentDocx
{
    private const string W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Types =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string Rels =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    public static byte[] Export(SliceBContractDocumentData? input)
    {
        if (input is null || input.ContractId == Guid.Empty ||
            input.BuildingId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.ContractNumber) ||
            string.IsNullOrWhiteSpace(input.BuildingName) ||
            string.IsNullOrWhiteSpace(input.BuildingAddress) ||
            string.IsNullOrWhiteSpace(input.RoomNumber) ||
            input.MonthlyRentVnd <= 0m || input.RequiredDepositVnd <= 0m ||
            input.StartDate == default || input.TenantNames is null ||
            input.TenantNames.Count > 100 ||
            input.TenantNames.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "Contract document export requires original authorized contract, room and building details.");

        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipXml(zip,"[Content_Types].xml",x=>
            {
                x.WriteStartElement("Types",Types);
                x.WriteStartElement("Default",Types);
                x.WriteAttributeString("Extension","rels");
                x.WriteAttributeString("ContentType",
                    "application/vnd.openxmlformats-package.relationships+xml");
                x.WriteEndElement();
                x.WriteStartElement("Default",Types);
                x.WriteAttributeString("Extension","xml");
                x.WriteAttributeString("ContentType","application/xml");
                x.WriteEndElement();
                x.WriteStartElement("Override",Types);
                x.WriteAttributeString("PartName","/word/document.xml");
                x.WriteAttributeString("ContentType",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
                x.WriteEndElement();
                x.WriteEndElement();
            });
            WriteZipXml(zip,"_rels/.rels",x=>
            {
                x.WriteStartElement("Relationships",Rels);
                x.WriteStartElement("Relationship",Rels);
                x.WriteAttributeString("Id","rId1");
                x.WriteAttributeString("Type",
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
                x.WriteAttributeString("Target","word/document.xml");
                x.WriteEndElement();
                x.WriteEndElement();
            });
            WriteZipXml(zip,"word/document.xml",x=>
            {
                x.WriteStartElement("w","document",W);
                x.WriteStartElement("w","body",W);
                Paragraph(x,"BẢN TRÍCH XUẤT DỮ LIỆU HỢP ĐỒNG",bold:true);
                Paragraph(x,"CHƯA PHẢI HỢP ĐỒNG ĐÃ KÝ • KHÔNG XÁC NHẬN VIỆC NHẬN TIỀN",bold:true);
                Paragraph(x,"Tài liệu được tạo từ thông tin đang lưu trong hệ thống để Owner đối chiếu, in và chuẩn bị hồ sơ. Không tự xác nhận chữ ký, chứng từ gốc hoặc điều khoản pháp lý chưa được hai bên ký.");
                Paragraph(x,"Mã hợp đồng: "+input.ContractNumber);
                Paragraph(x,"Tình trạng ghi nhận: "+input.ContractStatus);
                Paragraph(x,"Tòa nhà: "+input.BuildingName);
                Paragraph(x,"Địa chỉ: "+input.BuildingAddress);
                Paragraph(x,"Phòng: "+input.RoomNumber);
                Paragraph(x,"Bắt đầu: "+Date(input.StartDate));
                Paragraph(x,"Kết thúc dự kiến: "+
                    (input.EndDate.HasValue ? Date(input.EndDate.Value) :
                        "Chưa ghi nhận trong hợp đồng"));
                Paragraph(x,"Tiền thuê tháng: "+Vnd(input.MonthlyRentVnd));
                Paragraph(x,"Tiền cọc theo điều khoản hợp đồng: "+
                    Vnd(input.RequiredDepositVnd));
                Paragraph(x,"Cọc theo hợp đồng không có nghĩa là đã thu/đối soát tiền.",bold:true);
                if (input.RenewedFromContractId.HasValue)
                    Paragraph(x,"Hợp đồng tiền nhiệm (mã nội bộ): "+
                        input.RenewedFromContractId.Value.ToString("D"));
                Paragraph(x,"NGƯỜI THUÊ TRONG DANH SÁCH HỢP ĐỒNG",bold:true);
                if (input.TenantNames.Count == 0)
                    Paragraph(x,"Chưa có thông tin người thuê gắn với hợp đồng.");
                else
                {
                    for(var i=0;i<input.TenantNames.Count;i++)
                        Paragraph(x,$"{i+1}. {input.TenantNames[i]}");
                }
                Paragraph(x,"Các bên kiểm tra lại danh sách cư dân, giấy tờ, giá dịch vụ từng phòng và điều khoản trước khi ký.");
                Paragraph(x,"Chủ sở hữu / Đại diện quản lý: ____________________",bold:true);
                Paragraph(x,"Người thuê: ____________________",bold:true);
                Paragraph(x,"Ngày ký và xác nhận hồ sơ gốc: ____________________");
                Paragraph(x,"Mã tham chiếu nội bộ: "+input.ContractId.ToString("D"));
                x.WriteStartElement("w","sectPr",W);
                x.WriteStartElement("w","pgSz",W);
                x.WriteAttributeString("w","w",W,"11906");
                x.WriteAttributeString("w","h",W,"16838");
                x.WriteEndElement();
                x.WriteStartElement("w","pgMar",W);
                x.WriteAttributeString("w","top",W,"1134");
                x.WriteAttributeString("w","right",W,"1134");
                x.WriteAttributeString("w","bottom",W,"1134");
                x.WriteAttributeString("w","left",W,"1134");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();
            });
        }
        return bytes.ToArray();
    }

    private static string Date(DateTime value)
        => value.ToString("dd/MM/yyyy",CultureInfo.GetCultureInfo("vi-VN"));

    private static string Vnd(decimal amount)
        => amount.ToString("N0",CultureInfo.GetCultureInfo("vi-VN"))+" VND";

    private static void Paragraph(XmlWriter x,string value,bool bold=false)
    {
        x.WriteStartElement("w","p",W);
        x.WriteStartElement("w","pPr",W);
        x.WriteStartElement("w","spacing",W);
        x.WriteAttributeString("w","after",W,"150");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("w","r",W);
        x.WriteStartElement("w","rPr",W);
        x.WriteStartElement("w","rFonts",W);
        x.WriteAttributeString("w","ascii",W,"Arial");
        x.WriteAttributeString("w","hAnsi",W,"Arial");
        x.WriteEndElement();
        if(bold)
        {
            x.WriteStartElement("w","b",W);
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteStartElement("w","t",W);
        x.WriteAttributeString("xml","space",
            "http://www.w3.org/XML/1998/namespace","preserve");
        x.WriteString(value);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void WriteZipXml(
        ZipArchive zip,string name,Action<XmlWriter> write)
    {
        var entry=zip.CreateEntry(name,CompressionLevel.Optimal);
        using var stream=entry.Open();
        using var writer=XmlWriter.Create(stream,new XmlWriterSettings {
            Encoding=new UTF8Encoding(false),CloseOutput=false,
            OmitXmlDeclaration=false,Indent=false
        });
        writer.WriteStartDocument();
        write(writer);
        writer.WriteEndDocument();
    }
}

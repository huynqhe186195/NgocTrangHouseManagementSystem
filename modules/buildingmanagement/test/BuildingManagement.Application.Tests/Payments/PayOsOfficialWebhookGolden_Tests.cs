using System.Text.Json;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsOfficialWebhookGolden_Tests
{
    // Official payOS payment-request webhook example:
    // https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/
    // This is a PUBLIC DOCUMENTATION checksum key, never a live credential.
    private const string SampleChecksumKey =
        "1a54716c8f0efb2744fb28b6e38b25da7f67a925d98bc1c18bd8faaecadd7675";

    private const string OfficialSample = """
    {
      "code":"00","desc":"success","success":true,
      "data":{
        "orderCode":123,"amount":3000,"description":"VQRIO123",
        "accountNumber":"12345678","reference":"TF230204212323",
        "transactionDateTime":"2023-02-04 18:25:00",
        "currency":"VND","paymentLinkId":"124c33293c43417ab7879e14c8d9eb18",
        "code":"00","desc":"Thành công",
        "counterAccountBankId":"","counterAccountBankName":"",
        "counterAccountName":"","counterAccountNumber":"",
        "virtualAccountName":"","virtualAccountNumber":""
      },
      "signature":"412e915d2871504ed31be63c8f62a149a4410d34c4c42affc9006ef9917eaa03"
    }
    """;

    [Fact]
    public void Payos_official_documentation_signature_and_intent_match()
    {
        using var sample = JsonDocument.Parse(OfficialSample);
        Assert.True(PayOsPaymentWebhookSignature.IsValid(sample.RootElement, SampleChecksumKey));
        Assert.True(PayOsPaymentConfirmationGuard.Matches(
            sample.RootElement, 123, 3000,
            "124c33293c43417ab7879e14c8d9eb18", SampleChecksumKey));
    }

    [Fact]
    public void Duplicate_json_properties_cannot_be_used_to_confuse_business_checks()
    {
        const string ambiguous = """
        {
          "code":"00","success":true,
          "data":{"amount":3000,"amount":999999,"orderCode":123},
          "signature":"0000000000000000000000000000000000000000000000000000000000000000"
        }
        """;
        using var document = JsonDocument.Parse(ambiguous);
        Assert.False(PayOsPaymentWebhookSignature.IsValid(
            document.RootElement, SampleChecksumKey));
    }
}

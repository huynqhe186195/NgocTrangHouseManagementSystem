using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsPaymentLinkClient_Tests
{
    private static PayOsCheckoutCommand ValidCommand() =>
        new(123, 150000, "ROOM301", new Uri("https://example.test/return"),
            new Uri("https://example.test/cancel"), DateTime.UtcNow.AddMinutes(20));

    [Fact]
    public async Task Checkout_posts_signed_request_with_server_only_credentials()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api-merchant.payos.vn/v2/payment-requests", request.RequestUri!.AbsoluteUri);
            Assert.Equal("client-id", Assert.Single(request.Headers.GetValues("x-client-id")));
            Assert.Equal("api-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var data = json.RootElement;
            Assert.Equal(123L, data.GetProperty("orderCode").GetInt64());
            Assert.Equal(150000L, data.GetProperty("amount").GetInt64());
            Assert.Equal("ROOM301", data.GetProperty("description").GetString());
            Assert.Equal(
                PayOsPaymentRequestSignature.Create(
                    123, 150000, "ROOM301", new Uri("https://example.test/return"),
                    new Uri("https://example.test/cancel"), "checksum"),
                data.GetProperty("signature").GetString());
            return Ok(CreateResponse());
        });
        using var http = new HttpClient(handler);
        var sut = new PayOsPaymentLinkClient(http, "client-id", "api-key", "checksum");
        var result = await sut.CreateAsync(ValidCommand());
        Assert.Equal("LINK123", result.PaymentLinkId);
        Assert.Equal("https://pay.payos.vn/web/LINK123", result.CheckoutUrl.AbsoluteUri);
        Assert.Equal("QR-DATA", result.QrCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(124, 150000, "VND", "PENDING", "https://pay.payos.vn/web/LINK123")]
    [InlineData(123, 149999, "VND", "PENDING", "https://pay.payos.vn/web/LINK123")]
    [InlineData(123, 150000, "USD", "PENDING", "https://pay.payos.vn/web/LINK123")]
    [InlineData(123, 150000, "VND", "PAID", "https://pay.payos.vn/web/LINK123")]
    [InlineData(123, 150000, "VND", "PENDING", "https://evil.example/web/LINK123")]
    public async Task Checkout_rejects_mismatched_provider_response(
        long order, long amount, string currency, string status, string url)
    {
        using var http = new HttpClient(new StubHandler(_ =>
            Task.FromResult(Ok(CreateResponse(order, amount, currency, status, url)))));
        var sut = new PayOsPaymentLinkClient(http, "client-id", "api-key", "checksum");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CreateAsync(ValidCommand()));
    }

    [Fact]
    public async Task Reconciliation_is_read_only_and_rejects_wrong_order()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api-merchant.payos.vn/v2/payment-requests/123",
                request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Ok(
                "{\"code\":\"00\",\"data\":{\"id\":\"LINK123\",\"orderCode\":123," +
                "\"amount\":150000,\"amountPaid\":150000,\"amountRemaining\":0,\"status\":\"PAID\"}}"));
        });
        using var http = new HttpClient(handler);
        var sut = new PayOsPaymentLinkClient(http, "client-id", "api-key", "checksum");
        var result = await sut.GetAsync(123);
        Assert.Equal("PAID", result.Status);
        Assert.Equal(150000, result.AmountPaidVnd);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Reconciliation_rejects_wrong_provider_order()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            Task.FromResult(Ok(
                "{\"code\":\"00\",\"data\":{\"id\":\"LINK123\",\"orderCode\":999," +
                "\"amount\":150000,\"amountPaid\":150000,\"amountRemaining\":0,\"status\":\"PAID\"}}"))));
        var sut = new PayOsPaymentLinkClient(http, "client-id", "api-key", "checksum");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetAsync(123));
    }

    private static string CreateResponse(long order = 123, long amount = 150000,
        string currency = "VND", string status = "PENDING",
        string checkoutUrl = "https://pay.payos.vn/web/LINK123") =>
        JsonSerializer.Serialize(new
        {
            code = "00",
            data = new
            {
                orderCode = order,
                amount,
                currency,
                status,
                paymentLinkId = "LINK123",
                checkoutUrl,
                qrCode = "QR-DATA"
            }
        });

    private static HttpResponseMessage Ok(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public int Calls { get; private set; }

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return await _handler(request);
        }
    }
}

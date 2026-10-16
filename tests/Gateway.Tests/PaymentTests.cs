using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

[Collection("gateway")]
public class PaymentTests(GatewayFixture f) : IAsyncLifetime
{
    private static readonly object Body = new { msisdn = "263771234567", amountCents = 2500, currency = "USD" };

    public Task InitializeAsync() => f.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpRequestMessage Pay(string key, object? body = null) => new(HttpMethod.Post, "/payments")
    {
        Headers = { { "Idempotency-Key", key } },
        Content = JsonContent.Create(body ?? Body),
    };

    private static async Task<PaymentView> Read(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<PaymentView>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    private Task<Payment> Load(Guid id) =>
        f.WithScope(sp => sp.GetRequiredService<GatewayDbContext>().Payments.AsNoTracking().SingleAsync(p => p.Id == id));

    private Task<ReconcileSummary> Reconcile() =>
        f.WithScope(sp => sp.GetRequiredService<ReconciliationService>().RunOnceAsync(default, TimeSpan.Zero));

    // Idempotency under concurrency -------------------------------------------------------

    [Fact]
    public async Task Two_hundred_identical_requests_at_once_charge_the_customer_once()
    {
        f.Provider.Latency = TimeSpan.FromMilliseconds(200);
        var client = f.Client();

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => client.SendAsync(Pay("order-1001"))));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var ids = (await Task.WhenAll(responses.Select(Read))).Select(p => p.Id).Distinct();
        Assert.Single(ids);
        Assert.Equal(1, Assert.Single(f.Provider.Submissions).Value);
    }

    [Fact]
    public async Task A_thousand_requests_across_a_hundred_keys_make_exactly_a_hundred_charges()
    {
        var client = f.Client();
        var keys = Enumerable.Range(0, 100).Select(i => $"order-{i}").ToArray();
        var requests = Enumerable.Range(0, 1000).Select(i => client.SendAsync(Pay(keys[i % 100])));

        var responses = await Task.WhenAll(requests);

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        Assert.Equal(100, f.Provider.Submissions.Count);
        Assert.All(f.Provider.Submissions, kv => Assert.Equal(1, kv.Value));
        var stored = await f.WithScope(sp => sp.GetRequiredService<GatewayDbContext>().Payments.CountAsync());
        Assert.Equal(100, stored);
    }

    [Fact]
    public async Task A_retry_returns_the_original_payment_and_says_so()
    {
        var client = f.Client();
        var first = await client.SendAsync(Pay("order-2001"));
        var retry = await client.SendAsync(Pay("order-2001"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await Read(first)).Id, (await Read(retry)).Id);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_amount_is_rejected()
    {
        var client = f.Client();
        await client.SendAsync(Pay("order-3001"));
        var reused = await client.SendAsync(Pay("order-3001", new { msisdn = "263771234567", amountCents = 9900, currency = "USD" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(1, f.Provider.Submissions.Values.Sum());
    }

    // Validation and authentication --------------------------------------------------------

    [Fact]
    public async Task Requests_without_a_key_api_key_or_valid_body_are_refused()
    {
        var client = f.Client();
        var noKey = await client.PostAsJsonAsync("/payments", Body);
        var badBody = await client.SendAsync(Pay("order-4001", new { msisdn = "abc", amountCents = 0, currency = "EUR" }));
        var anon = await f.App.CreateClient().SendAsync(Pay("order-4002"));

        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        Assert.Contains("msisdn", await badBody.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Empty(f.Provider.Submissions);
    }

    [Fact]
    public async Task A_merchant_cannot_read_another_merchants_payment()
    {
        var created = await Read(await f.Client().SendAsync(Pay("order-4101")));
        var other = f.App.CreateClient();
        other.DefaultRequestHeaders.Add("X-Api-Key", "not-a-real-key");

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync($"/payments/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client().GetAsync($"/payments/{created.Id}")).StatusCode);
    }

    // Callbacks ----------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Callback(object status, string? signature = null)
    {
        var body = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var msg = new HttpRequestMessage(HttpMethod.Post, "/webhooks/provider") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        msg.Headers.Add("X-Provider-Signature", signature ?? PaymentService.Sign(body, GatewayFixture.WebhookSecret));
        return await f.App.CreateClient().SendAsync(msg);
    }

    [Fact]
    public async Task A_signed_callback_settles_the_payment_and_a_duplicate_changes_nothing()
    {
        var p = await Read(await f.Client().SendAsync(Pay("order-5001")));
        var reference = PaymentService.Reference(p.Id);
        var success = new { reference, providerReference = "MP-1", status = "succeeded", amountCents = 2500 };

        Assert.Equal(HttpStatusCode.OK, (await Callback(success)).StatusCode);
        Assert.Equal(PaymentStatus.Succeeded, (await Load(p.Id)).Status);

        // A late "failed" for the same charge must not undo a final status.
        await Callback(new { reference, providerReference = "MP-1", status = "failed", amountCents = 2500, reason = "late" });
        Assert.Equal(PaymentStatus.Succeeded, (await Load(p.Id)).Status);
    }

    [Fact]
    public async Task A_callback_with_a_bad_signature_is_refused()
    {
        var p = await Read(await f.Client().SendAsync(Pay("order-5101")));
        var forged = new { reference = PaymentService.Reference(p.Id), providerReference = "MP-1", status = "succeeded", amountCents = 2500 };

        Assert.Equal(HttpStatusCode.Unauthorized, (await Callback(forged, signature: "deadbeef")).StatusCode);
        Assert.Equal(PaymentStatus.Pending, (await Load(p.Id)).Status);
    }

    [Fact]
    public async Task A_settlement_for_the_wrong_amount_is_reversed()
    {
        var p = await Read(await f.Client().SendAsync(Pay("order-5201")));
        var reference = PaymentService.Reference(p.Id);

        await Callback(new { reference, providerReference = "MP-1", status = "succeeded", amountCents = 2600 });

        var stored = await Load(p.Id);
        Assert.Equal(PaymentStatus.Reversed, stored.Status);
        Assert.Contains(reference, f.Provider.Reversals);
    }

    // Reconciliation -----------------------------------------------------------------------

    [Fact]
    public async Task A_timed_out_charge_the_provider_never_saw_is_failed_without_charging()
    {
        f.Provider.Behaviour = _ => new(SubmitOutcome.Unknown, Reason: "no response from provider");
        var p = await Read(await f.Client().SendAsync(Pay("order-6001")));
        Assert.Equal("Unknown", p.Status);

        await Reconcile();

        var stored = await Load(p.Id);
        Assert.Equal(PaymentStatus.Failed, stored.Status);
        Assert.Contains("not charged", stored.FailureReason);
        Assert.Equal(1, f.Provider.Submissions.Values.Sum()); // never resent
    }

    [Fact]
    public async Task A_timed_out_charge_the_provider_did_take_is_settled_not_resent()
    {
        f.Provider.Behaviour = r =>
        {
            // The provider charged the customer but the response never arrived.
            f.Provider.Charges[r.Reference] = new(r.Reference, "MP-9", "succeeded", r.AmountCents, null);
            return new(SubmitOutcome.Unknown, Reason: "no response from provider");
        };
        var p = await Read(await f.Client().SendAsync(Pay("order-6101")));

        await Reconcile();

        Assert.Equal(PaymentStatus.Succeeded, (await Load(p.Id)).Status);
        Assert.Equal(1, f.Provider.Submissions.Values.Sum());
    }

    [Fact]
    public async Task A_payment_whose_callback_was_lost_is_settled_by_reconciliation()
    {
        var p = await Read(await f.Client().SendAsync(Pay("order-6201")));
        var reference = PaymentService.Reference(p.Id);
        f.Provider.Charges[reference] = new(reference, "MP-7", "failed", 2500, "insufficient funds");

        var summary = await Reconcile();

        Assert.Equal(1, summary.Settled);
        var stored = await Load(p.Id);
        Assert.Equal(PaymentStatus.Failed, stored.Status);
        Assert.Equal("insufficient funds", stored.FailureReason);
    }

    [Fact]
    public async Task A_provider_outage_during_reconciliation_leaves_payments_untouched_for_the_next_run()
    {
        var p = await Read(await f.Client().SendAsync(Pay("order-6301")));
        f.Provider.StatusFailure = new HttpRequestException("provider unreachable");

        var summary = await Reconcile();

        Assert.Equal(1, summary.StillPending);
        Assert.Equal(PaymentStatus.Pending, (await Load(p.Id)).Status);

        f.Provider.StatusFailure = null;
        var reference = PaymentService.Reference(p.Id);
        f.Provider.Charges[reference] = new(reference, "MP-8", "succeeded", 2500, null);
        await Reconcile();
        Assert.Equal(PaymentStatus.Succeeded, (await Load(p.Id)).Status);
    }

    [Fact]
    public async Task Two_reconcilers_running_at_once_never_handle_the_same_payment()
    {
        var client = f.Client();
        var created = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => client.SendAsync(Pay($"order-7{i:D3}"))));
        foreach (var r in created)
        {
            var p = await Read(r);
            var reference = PaymentService.Reference(p.Id);
            f.Provider.Charges[reference] = new(reference, "MP", "succeeded", 2500, null);
        }

        var runs = await Task.WhenAll(Reconcile(), Reconcile());

        Assert.Equal(40, runs.Sum(r => r.Checked));
        var attempts = await f.WithScope(sp => sp.GetRequiredService<GatewayDbContext>().Payments.Select(p => p.ReconcileAttempts).ToListAsync());
        Assert.All(attempts, a => Assert.Equal(1, a));
    }
}

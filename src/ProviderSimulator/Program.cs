using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ProviderSimulator;

// A deliberately unreliable mobile money provider, in the style of EcoCash or OneMoney.
// It charges every request it receives, even repeats of the same reference, so the
// gateway alone must make sure a customer is charged once.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(builder.Configuration.GetSection("Simulator").Get<SimulatorOptions>() ?? new SimulatorOptions());
builder.Services.AddSingleton<Ledger>();
builder.Services.AddHttpClient("callbacks");
var app = builder.Build();

app.MapPost("/v1/charges", async (ChargeIn req, Ledger ledger, SimulatorOptions o, IHttpClientFactory http, CancellationToken ct) =>
{
    var rng = Random.Shared;
    await Task.Delay(rng.Next(o.MinLatencyMs, o.MaxLatencyMs), ct);

    if (rng.NextDouble() < o.ErrorBeforeChargeRate)
        return Results.StatusCode(503); // failed before anything was recorded

    var charge = ledger.Record(req);

    // Settlement is scheduled as soon as the charge is recorded, independent of this
    // request: a real provider settles a charge even if the caller has hung up.
    _ = Task.Run(async () =>
    {
        await Task.Delay(rng.Next(500, 3000));
        ledger.Settle(charge, rng.NextDouble() < o.InsufficientFundsRate, rng.NextDouble() < o.WrongAmountRate);
        if (rng.NextDouble() >= o.LostCallbackRate && req.CallbackUrl is { Length: > 0 })
        {
            var body = JsonSerializer.Serialize(charge.ToStatus(), Json.Options);
            var msg = new HttpRequestMessage(HttpMethod.Post, req.CallbackUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            msg.Headers.Add("X-Provider-Signature", Signing.Sign(body, o.WebhookSecret));
            try { await http.CreateClient("callbacks").SendAsync(msg); } catch { /* gateway down: reconciliation will find it */ }
        }
    });

    if (rng.NextDouble() < o.HangAfterChargeRate)
    {
        // The customer has been charged, but the gateway won't hear back in time.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { }
    }

    return Results.Accepted(value: new { providerReference = charge.ProviderReference });
});

app.MapGet("/v1/charges/{reference}", (string reference, Ledger ledger) =>
    ledger.Latest(reference) is { } c ? Results.Ok(c.ToStatus()) : Results.NotFound());

app.MapPost("/v1/charges/{reference}/reverse", (string reference, Ledger ledger) =>
    ledger.Reverse(reference) ? Results.Ok() : Results.NotFound());

// What the load test checks: how many references were debited more than once.
app.MapGet("/v1/stats", (Ledger ledger) => ledger.Stats());

app.MapPost("/v1/reset", (Ledger ledger) => { ledger.Clear(); return Results.NoContent(); });

app.Run();

public partial class Program;

namespace ProviderSimulator
{
    public record ChargeIn(string Reference, string Msisdn, long AmountCents, string Currency, string? CallbackUrl);

    public class SimulatorOptions
    {
        public int MinLatencyMs { get; set; } = 50;
        public int MaxLatencyMs { get; set; } = 400;
        public double ErrorBeforeChargeRate { get; set; } = 0.05;
        public double HangAfterChargeRate { get; set; } = 0.03;
        public double LostCallbackRate { get; set; } = 0.10;
        public double InsufficientFundsRate { get; set; } = 0.10;
        public double WrongAmountRate { get; set; } = 0.01;
        public string WebhookSecret { get; set; } = "dev-webhook-secret";
    }

    public class Charge
    {
        public required string Reference { get; init; }
        public required string ProviderReference { get; init; }
        public required long RequestedCents { get; init; }
        public long SettledCents { get; set; }
        public string Status { get; set; } = "pending";
        public string? Reason { get; set; }

        public object ToStatus() => new { Reference, ProviderReference, Status, AmountCents = Status == "pending" ? RequestedCents : SettledCents, Reason };
    }

    public class Ledger
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<Charge>> _byReference = new();
        private long _sequence;

        public Charge Record(ChargeIn req)
        {
            var charge = new Charge
            {
                Reference = req.Reference,
                ProviderReference = $"MP{Interlocked.Increment(ref _sequence):D9}",
                RequestedCents = req.AmountCents,
            };
            _byReference.GetOrAdd(req.Reference, _ => new()).Enqueue(charge);
            return charge;
        }

        public void Settle(Charge c, bool insufficientFunds, bool wrongAmount)
        {
            lock (c)
            {
                if (c.Status != "pending") return;
                if (insufficientFunds) { c.Status = "failed"; c.Reason = "insufficient funds"; return; }
                c.Status = "succeeded";
                c.SettledCents = wrongAmount ? c.RequestedCents + 100 : c.RequestedCents;
            }
        }

        public Charge? Latest(string reference) => _byReference.TryGetValue(reference, out var q) ? q.LastOrDefault() : null;

        public bool Reverse(string reference)
        {
            if (Latest(reference) is not { } c) return false;
            lock (c) { c.Status = "reversed"; c.Reason = "reversed at the merchant's request"; }
            return true;
        }

        public object Stats()
        {
            var all = _byReference.Values.SelectMany(q => q).ToList();
            return new
            {
                charges = all.Count,
                references = _byReference.Count,
                referencesChargedMoreThanOnce = _byReference.Count(kv => kv.Value.Count > 1),
                succeeded = all.Count(c => c.Status == "succeeded"),
                debitedCents = all.Where(c => c.Status == "succeeded").Sum(c => c.SettledCents),
            };
        }

        public void Clear() => _byReference.Clear();
    }

    public static class Signing
    {
        public static string Sign(string body, string secret) =>
            Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}

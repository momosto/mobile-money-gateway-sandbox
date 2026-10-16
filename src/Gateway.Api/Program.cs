using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGatewayCore(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Mobile Money Gateway", Version = "v1", Description = "Idempotent mobile money payments: a customer is charged once, however many times a request is retried." }));

var app = builder.Build();
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.Migrate();
}

var payments = app.MapGroup("/payments").AddEndpointFilter(RequireMerchant);

payments.MapPost("/", async (HttpContext http, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CreatePayment body, PaymentService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        return Problem(400, "Idempotency-Key header required", "Send a unique key of up to 128 characters with every payment; retries must reuse it.");
    if (Validate(body) is { } invalid) return invalid;

    var result = await service.CreateAsync(Merchant(http), idempotencyKey, body, ct);
    return result.Outcome switch
    {
        CreateOutcome.Created => Results.Created($"/payments/{result.Payment!.Id}", PaymentView.From(result.Payment)),
        CreateOutcome.Replayed => Replay(http, result.Payment!),
        _ => Problem(422, "Idempotency-Key already used for a different payment", "This key was first used with a different number, amount or currency. Use a new key for a new payment."),
    };
}).WithSummary("Charge a customer's mobile money wallet").Produces<PaymentView>(201).Produces<PaymentView>(200);

payments.MapGet("/{id:guid}", async (HttpContext http, Guid id, GatewayDbContext db, CancellationToken ct) =>
{
    var merchant = Merchant(http);
    return await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.MerchantId == merchant, ct) is { } p
        ? Results.Ok(PaymentView.From(p))
        : Problem(404, "Payment not found", $"No payment {id} for this merchant.");
}).WithSummary("Get one payment");

payments.MapGet("/", async (HttpContext http, PaymentStatus? status, int? limit, GatewayDbContext db, CancellationToken ct) =>
{
    var merchant = Merchant(http);
    var query = db.Payments.AsNoTracking().Where(p => p.MerchantId == merchant);
    if (status is not null) query = query.Where(p => p.Status == status);
    var rows = await query.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
    return Results.Ok(rows.Select(PaymentView.From));
}).WithSummary("List recent payments");

app.MapPost("/webhooks/provider", async (HttpRequest request, GatewayOptions options, PaymentService service, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync(ct);
    if (!PaymentService.SignatureIsValid(body, request.Headers["X-Provider-Signature"], options.WebhookSecret))
        return Problem(401, "Invalid signature", "The X-Provider-Signature header doesn't match the body.");

    var status = JsonSerializer.Deserialize<ChargeStatus>(body, WebJson);
    if (status is null) return Problem(400, "Invalid callback body", "Expected a charge status.");
    return await service.SettleAsync(status, ct) is null ? Problem(404, "Unknown reference", status.Reference) : Results.Ok();
}).WithSummary("Provider callback (HMAC-SHA256 signed)").ExcludeFromDescription();

app.MapGet("/health", async (GatewayDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));

app.Run();

static string Merchant(HttpContext http) => (string)http.Items["merchant"]!;

static async ValueTask<object?> RequireMerchant(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var http = ctx.HttpContext;
    var options = http.RequestServices.GetRequiredService<GatewayOptions>();
    if (!options.ApiKeys.TryGetValue(http.Request.Headers["X-Api-Key"].ToString(), out var merchant))
        return Problem(401, "Missing or invalid API key", "Send your merchant key in the X-Api-Key header.");
    http.Items["merchant"] = merchant;
    return await next(ctx);
}

static IResult Replay(HttpContext http, Payment payment)
{
    http.Response.Headers["Idempotent-Replayed"] = "true";
    return Results.Ok(PaymentView.From(payment));
}

static IResult? Validate(CreatePayment body)
{
    var errors = new Dictionary<string, string[]>();
    if (!Regex.IsMatch(body.Msisdn ?? "", @"^\d{9,15}$")) errors["msisdn"] = ["Use the wallet number in international format, digits only, e.g. 263771234567."];
    if (body.AmountCents is <= 0 or > 100_000_00) errors["amountCents"] = ["Amount must be between 1 cent and 100,000.00."];
    if (!Regex.IsMatch(body.Currency ?? "", "^(USD|ZWG)$")) errors["currency"] = ["Currency must be USD or ZWG."];
    return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
}

static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);

public record PaymentView(Guid Id, string IdempotencyKey, string Msisdn, long AmountCents, string Currency, string Status, string? ProviderReference, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static PaymentView From(Payment p) => new(p.Id, p.IdempotencyKey, p.Msisdn, p.AmountCents, p.Currency, p.Status.ToString(), p.ProviderReference, p.FailureReason, p.CreatedAt, p.UpdatedAt);
}

public partial class Program
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}

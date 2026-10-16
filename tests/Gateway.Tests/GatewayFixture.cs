using System.Collections.Concurrent;
using Gateway.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Gateway.Tests;

/// <summary>
/// A fake provider that counts every charge it is asked to make, like a real provider
/// with no idempotency of its own. Tests assert on these counts.
/// </summary>
public class FakeProvider : IProviderClient
{
    public readonly ConcurrentDictionary<string, int> Submissions = new();
    public readonly ConcurrentDictionary<string, ChargeStatus> Charges = new();
    public readonly ConcurrentBag<string> Reversals = new();

    /// <summary>What the next submits do. "accept" by default.</summary>
    public Func<ChargeRequest, SubmitResult> Behaviour { get; set; } = _ => new(SubmitOutcome.Accepted);
    /// <summary>Delay inside Submit, to widen the race window for concurrency tests.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(50);
    /// <summary>When set, status queries throw this, as if the provider were down.</summary>
    public Exception? StatusFailure { get; set; }

    public async Task<SubmitResult> SubmitAsync(ChargeRequest request, CancellationToken ct)
    {
        Submissions.AddOrUpdate(request.Reference, 1, (_, n) => n + 1);
        await Task.Delay(Latency, ct);
        var result = Behaviour(request);
        if (result.Outcome == SubmitOutcome.Accepted)
            Charges[request.Reference] = new(request.Reference, $"MP-{request.Reference[..8]}", "pending", request.AmountCents, null);
        return result with { ProviderReference = result.ProviderReference ?? $"MP-{request.Reference[..8]}" };
    }

    public Task<ChargeStatus?> GetStatusAsync(string reference, CancellationToken ct) =>
        StatusFailure is { } e ? Task.FromException<ChargeStatus?>(e) : Task.FromResult(Charges.TryGetValue(reference, out var c) ? c : null);

    public Task<bool> ReverseAsync(string reference, CancellationToken ct)
    {
        Reversals.Add(reference);
        return Task.FromResult(true);
    }

    public void Reset()
    {
        Submissions.Clear();
        Charges.Clear();
        Reversals.Clear();
        Behaviour = _ => new(SubmitOutcome.Accepted);
        Latency = TimeSpan.FromMilliseconds(50);
        StatusFailure = null;
    }
}

public class GatewayFixture : IAsyncLifetime
{
    public const string ApiKey = "test-merchant-key";
    public const string WebhookSecret = "test-webhook-secret";

    // GATEWAY_TEST_DB points at an existing PostgreSQL (for machines without Docker);
    // otherwise Testcontainers starts a throwaway PostgreSQL 16, as CI does.
    private static readonly string? ExternalDb = Environment.GetEnvironmentVariable("GATEWAY_TEST_DB");
    private readonly PostgreSqlContainer? _db = ExternalDb is null ? new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build() : null;
    public FakeProvider Provider { get; } = new();
    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (_db is not null) await _db.StartAsync();
        var connection = ExternalDb ?? _db!.GetConnectionString();
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Gateway", connection + ";Maximum Pool Size=200");
            b.UseSetting("Gateway:WebhookSecret", WebhookSecret);
            b.UseSetting($"Gateway:ApiKeys:{ApiKey}", "merchant-test");
            b.ConfigureTestServices(s => s.AddSingleton<IProviderClient>(Provider));
        });
        _ = App.Server; // starts the app, which applies migrations
    }

    public HttpClient Client()
    {
        var c = App.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return c;
    }

    public async Task<T> WithScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task ResetAsync()
    {
        Provider.Reset();
        return WithScope(async sp => await sp.GetRequiredService<GatewayDbContext>().Payments.ExecuteDeleteAsync());
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        if (_db is not null) await _db.DisposeAsync();
    }
}

[CollectionDefinition("gateway")]
public class GatewayCollection : ICollectionFixture<GatewayFixture>;

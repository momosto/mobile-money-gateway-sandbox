using System.Net;
using System.Net.Http.Json;

namespace Gateway.Core;

public record ChargeRequest(string Reference, string Msisdn, long AmountCents, string Currency, string CallbackUrl);
public record ChargeAccepted(string ProviderReference);
public record ChargeStatus(string Reference, string ProviderReference, string Status, long AmountCents, string? Reason);

public enum SubmitOutcome { Accepted, Declined, Unknown }

public record SubmitResult(SubmitOutcome Outcome, string? ProviderReference = null, string? Reason = null);

/// <summary>The mobile money provider's API: initiate a charge, query it, reverse it.</summary>
public interface IProviderClient
{
    Task<SubmitResult> SubmitAsync(ChargeRequest request, CancellationToken ct);
    /// <summary>Null when the provider has never seen this reference.</summary>
    Task<ChargeStatus?> GetStatusAsync(string reference, CancellationToken ct);
    Task<bool> ReverseAsync(string reference, CancellationToken ct);
}

public class HttpProviderClient(HttpClient http) : IProviderClient
{
    public async Task<SubmitResult> SubmitAsync(ChargeRequest request, CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync("/v1/charges", request, ct);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var body = await response.Content.ReadFromJsonAsync<ChargeAccepted>(ct);
                return new(SubmitOutcome.Accepted, body?.ProviderReference);
            }
            if ((int)response.StatusCode is >= 400 and < 500)
                return new(SubmitOutcome.Declined, Reason: $"provider rejected the request ({(int)response.StatusCode})");
            // 5xx: the provider may or may not have recorded the charge.
            return new(SubmitOutcome.Unknown, Reason: $"provider error {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // A timeout is the dangerous case: the charge may have gone through.
            return new(SubmitOutcome.Unknown, Reason: "no response from provider");
        }
    }

    public async Task<ChargeStatus?> GetStatusAsync(string reference, CancellationToken ct)
    {
        var response = await http.GetAsync($"/v1/charges/{Uri.EscapeDataString(reference)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChargeStatus>(ct);
    }

    public async Task<bool> ReverseAsync(string reference, CancellationToken ct)
    {
        var response = await http.PostAsync($"/v1/charges/{Uri.EscapeDataString(reference)}/reverse", null, ct);
        return response.IsSuccessStatusCode;
    }
}

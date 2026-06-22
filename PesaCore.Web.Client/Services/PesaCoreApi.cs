using System.Net.Http.Json;

namespace PesaCore.Web.Client.Services;

// ===== Typed client for the PesaCore API, reached SAME-ORIGIN via the BFF =====
//
// Every call goes to /api/* on this origin. The BFF (PesaCore.Web) reverse-
// proxies that to the real PesaCore API, stripping the /api prefix. So:
//   GET  /api/Accounts/dto/linq   -> PesaCore GET  /Accounts/dto/linq
//   GET  /api/Cqrs/balance/{n}    -> PesaCore GET  /Cqrs/balance/{n}
//   POST /api/Cqrs/transfer       -> PesaCore POST /Cqrs/transfer
//
// No CORS, no API URL in the browser. The injected HttpClient's BaseAddress
// is the host origin (set in Program.cs).
//
// Errors are returned as values (ApiResult<T>) rather than thrown — the UI
// renders failure states explicitly instead of relying on try/catch in markup.
public sealed class PesaCoreApi
{
    private readonly HttpClient _http;

    public PesaCoreApi(HttpClient http) => _http = http;

    // GET /Accounts/dto/linq — full account rows incl. balance + tx count.
    // (The /best endpoint omits balance; the dashboard needs balance, so we
    //  use the LINQ-projection endpoint which returns the richer AccountDto.)
    public async Task<ApiResult<IReadOnlyList<AccountDto>>> GetAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _http.GetFromJsonAsync<List<AccountDto>>("api/Accounts/dto/linq", ct);
            return ApiResult<IReadOnlyList<AccountDto>>.Ok(data ?? new());
        }
        catch (Exception ex)
        {
            return ApiResult<IReadOnlyList<AccountDto>>.Fail(Describe(ex));
        }
    }

    // GET /Cqrs/balance/{accountNumber} — 200 with balance, or 404 if unknown.
    public async Task<ApiResult<AccountBalance>> GetBalanceAsync(string accountNumber, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"api/Cqrs/balance/{Uri.EscapeDataString(accountNumber)}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return ApiResult<AccountBalance>.Fail($"Account {accountNumber} not found.");
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadFromJsonAsync<AccountBalance>(cancellationToken: ct);
            return data is null
                ? ApiResult<AccountBalance>.Fail("Empty response from API.")
                : ApiResult<AccountBalance>.Ok(data);
        }
        catch (Exception ex)
        {
            return ApiResult<AccountBalance>.Fail(Describe(ex));
        }
    }

    // POST /Cqrs/transfer — REQUIRES X-Idempotency-Key (the IdempotencyBehavior
    // rejects keyless mutations with 400). We generate a fresh UUID v4 per
    // submit so each user-initiated transfer is a distinct operation, but a
    // network retry of the SAME submit could reuse it for true idempotency.
    public async Task<ApiResult<TransferResult>> TransferAsync(
        TransferRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "api/Cqrs/transfer")
            {
                Content = JsonContent.Create(request)
            };
            msg.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp = await _http.SendAsync(msg, ct);
            // The handler returns 200 on success and 400 on a business-rule
            // violation (insufficient funds / unknown account) — BOTH carry a
            // TransferResult body, so we read it regardless of status.
            var body = await resp.Content.ReadFromJsonAsync<TransferResult>(cancellationToken: ct);
            if (body is not null)
                return ApiResult<TransferResult>.Ok(body);

            return ApiResult<TransferResult>.Fail($"Transfer failed ({(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            return ApiResult<TransferResult>.Fail(Describe(ex));
        }
    }

    // POST /Cqrs/accounts — opens an account. REQUIRES X-Idempotency-Key (same
    // IdempotencyBehavior contract as transfer): a network retry of THIS submit reuses
    // the key for safe replay (no duplicate account), a fresh submit gets a new key.
    // Returns 201 on success and 400 on a validation/business failure — BOTH carry a
    // CreateAccountResult body, so we read it regardless of status.
    public async Task<ApiResult<CreateAccountResult>> CreateAccountAsync(
        CreateAccountRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "api/Cqrs/accounts")
            {
                Content = JsonContent.Create(request)
            };
            msg.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadFromJsonAsync<CreateAccountResult>(cancellationToken: ct);
            if (body is not null)
                return ApiResult<CreateAccountResult>.Ok(body);

            return ApiResult<CreateAccountResult>.Fail($"Create account failed ({(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            return ApiResult<CreateAccountResult>.Fail(Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        ex is HttpRequestException
            ? "Could not reach the PesaCore API through the gateway."
            : ex.Message;
}

// Lightweight result wrapper — errors as values, not exceptions in the UI layer.
public readonly record struct ApiResult<T>(bool Success, T? Value, string? Error)
{
    public static ApiResult<T> Ok(T value) => new(true, value, null);
    public static ApiResult<T> Fail(string error) => new(false, default, error);
}

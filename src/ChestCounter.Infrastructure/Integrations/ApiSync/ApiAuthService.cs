using System.Text;
using System.Text.Json;

namespace ChestCounter;

public enum ApiAuthState
{
    NotStarted,
    Obtaining,
    Authenticated,
    Expired,
    Failed,
}

public sealed record ApiAuthStatus(ApiAuthState State, string Detail, DateTimeOffset? ExpiresAtUtc);

public sealed class ApiAuthException : Exception
{
    public ApiAuthException(string message, bool notAuthorized = false, Exception? inner = null)
        : base(message, inner) => NotAuthorized = notAuthorized;

    /// <summary>True when no request was made because the app is not allowed to request a token.</summary>
    public bool NotAuthorized { get; }
}

/// <summary>
/// Keeps the API JWT in memory only. The token is never written to disk, logs or the UI.
/// </summary>
public sealed class ApiAuthService
{
    /// <summary>
    /// POST /auth/mock is absent from reference/openapi.yaml but was confirmed against the production
    /// API by the project owner. It needs no Authorization header and returns {success, token, user}.
    /// </summary>
    public const bool MockEndpointAuthorizedForProduction = true;

    public const string MockEndpoint = "auth/mock";
    public static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    private readonly bool _mockAuthorized;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private string? _token;
    private DateTimeOffset _expiresAt;
    private DateTimeOffset? _lastFailureAt;
    private ApiAuthStatus _status = new(ApiAuthState.NotStarted, "", null);

    public ApiAuthService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        bool mockEndpointAuthorized = MockEndpointAuthorizedForProduction,
        Func<DateTimeOffset>? clock = null)
    {
        _send = send;
        _mockAuthorized = mockEndpointAuthorized;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<ApiAuthStatus>? StatusChanged;

    public ApiAuthStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                if (_token != null && _status.State == ApiAuthState.Authenticated && _clock() >= _expiresAt - ExpirySkew)
                {
                    return _status with { State = ApiAuthState.Expired };
                }
                return _status;
            }
        }
    }

    /// <summary>The token currently held, exposed only so diagnostics can redact it.</summary>
    internal string? CurrentToken
    {
        get
        {
            lock (_stateGate)
            {
                return _token;
            }
        }
    }

    public bool IsInFailureCooldown
    {
        get
        {
            lock (_stateGate)
            {
                return _lastFailureAt is { } failedAt && _clock() - failedAt < FailureCooldown;
            }
        }
    }

    public async Task<string> GetTokenAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        if (TryGetValidToken(out var cached))
        {
            return cached;
        }

        // A single gate guarantees concurrent callers share one token request.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetValidToken(out cached))
            {
                return cached;
            }

            return await RequestTokenAsync(baseUri, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Discards the token after the API rejected it, unless it was already replaced.</summary>
    public void Invalidate(string rejectedToken)
    {
        lock (_stateGate)
        {
            if (_token != rejectedToken)
            {
                return;
            }
            _token = null;
            SetStatus(new ApiAuthStatus(ApiAuthState.Expired, "The API rejected the token.", null));
        }
    }

    private bool TryGetValidToken(out string token)
    {
        lock (_stateGate)
        {
            if (_token != null && _clock() < _expiresAt - ExpirySkew)
            {
                token = _token;
                return true;
            }
            if (_token != null)
            {
                _token = null;
                SetStatus(new ApiAuthStatus(ApiAuthState.Expired, "The token expired.", null));
            }
        }
        token = "";
        return false;
    }

    private async Task<string> RequestTokenAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        if (!_mockAuthorized)
        {
            const string message = "Automatic authorization is disabled in this configuration.";
            Fail(message);
            throw new ApiAuthException(message, notAuthorized: true);
        }

        SetStatus(new ApiAuthStatus(ApiAuthState.Obtaining, "", null));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, MockEndpoint));
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _send(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiAuthException($"The authorization request returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var token = ReadToken(body);
            var expiresAt = ReadExpiry(token);
            if (expiresAt - ExpirySkew <= _clock())
            {
                throw new ApiAuthException("The API returned an already expired token.");
            }

            lock (_stateGate)
            {
                _token = token;
                _expiresAt = expiresAt;
                _lastFailureAt = null;
                SetStatus(new ApiAuthStatus(ApiAuthState.Authenticated, "", expiresAt));
            }
            return token;
        }
        catch (ApiAuthException ex)
        {
            Fail(ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException
            or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var message = ex is OperationCanceledException
                ? "The authorization request timed out."
                : ex is HttpRequestException
                    ? "Could not reach the API to obtain authorization."
                    : "The authorization response was not valid.";
            Fail(message);
            throw new ApiAuthException(message, inner: ex);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the caller: leave the state as it was before the attempt.
            SetStatus(new ApiAuthStatus(
                _token == null ? ApiAuthState.NotStarted : ApiAuthState.Authenticated, "", _token == null ? null : _expiresAt));
            throw;
        }
    }

    private void Fail(string message)
    {
        lock (_stateGate)
        {
            _token = null;
            _lastFailureAt = _clock();
            SetStatus(new ApiAuthStatus(ApiAuthState.Failed, message, null));
        }
    }

    private void SetStatus(ApiAuthStatus status)
    {
        lock (_stateGate)
        {
            _status = status;
        }
        StatusChanged?.Invoke(status);
    }

    private static string ReadToken(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ApiAuthException("The authorization response was not valid.");
        }
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            throw new ApiAuthException("The API declined to issue a token.");
        }
        if (!root.TryGetProperty("token", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new ApiAuthException("The authorization response did not include a token.");
        }
        return tokenElement.GetString()!.Trim();
    }

    /// <summary>Reads the JWT exp claim. The signature is not verified: only the API can do that.</summary>
    internal static DateTimeOffset ReadExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
        {
            throw new ApiAuthException("The API returned a token with an invalid structure.");
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            throw new ApiAuthException("The API returned a token with an invalid structure.", inner: ex);
        }

        throw new ApiAuthException("The API returned a token without an expiration (exp) claim.");
    }
}

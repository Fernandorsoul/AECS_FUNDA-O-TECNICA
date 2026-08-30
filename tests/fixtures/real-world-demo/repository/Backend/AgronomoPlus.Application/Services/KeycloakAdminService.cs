using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgronomoPlus.Application.Services;

public interface IKeycloakAdminService
{
    Task<string> CreateUserAsync(
        string email,
        string firstName,
        string lastName,
        string[] roles,
        string password);

    Task DeleteUserAsync(string keycloakId);
}

public class KeycloakAdminService : IKeycloakAdminService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KeycloakAdminService> _logger;
    private string? _accessToken;

    public KeycloakAdminService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<KeycloakAdminService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> CreateUserAsync(
        string email,
        string firstName,
        string lastName,
        string[] roles,
        string password)
    {
        await EnsureTokenAsync();
        var response = await _httpClient.PostAsJsonAsync(
            $"{AdminUrl}/admin/realms/{Realm}/users",
            new
            {
                username = email,
                email,
                enabled = true,
                firstName,
                lastName,
                credentials = new[] { new { type = "password", value = password, temporary = false } }
            });
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not create Keycloak user: {StatusCode}", response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        return response.Headers.Location?.Segments.Last()
            ?? throw new InvalidOperationException("Keycloak did not return the created user id.");
    }

    public async Task DeleteUserAsync(string keycloakId)
    {
        await EnsureTokenAsync();
        await _httpClient.DeleteAsync($"{AdminUrl}/admin/realms/{Realm}/users/{keycloakId}");
    }

    private string AdminUrl => _configuration["KeycloakAdmin:Url"] ?? string.Empty;
    private string Realm => _configuration["KeycloakAdmin:Realm"] ?? string.Empty;

    private async Task EnsureTokenAsync()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _configuration["KeycloakAdmin:ClientId"] ?? "admin-cli",
            ["username"] = _configuration["KeycloakAdmin:Username"] ?? string.Empty,
            ["password"] = _configuration["KeycloakAdmin:Password"] ?? string.Empty
        });
        var response = await _httpClient.PostAsync(
            $"{AdminUrl}/realms/master/protocol/openid-connect/token",
            content);
        response.EnsureSuccessStatusCode();
        _accessToken = (await response.Content.ReadFromJsonAsync<TokenResponse>())?.AccessToken;
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}

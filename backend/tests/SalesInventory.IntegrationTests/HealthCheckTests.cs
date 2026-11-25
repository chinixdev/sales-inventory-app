using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SalesInventory.IntegrationTests;

public class HealthCheckTests : IClassFixture<JwtConfiguredWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthCheckTests(JwtConfiguredWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealth_NativeEndpoint_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetApiHealth_ControllerEndpoint_ReturnsOkWithDetails()
    {
        var response = await _client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        json.Should().NotBeNull();
        json!["status"]!.ToString().Should().Be("Healthy");
        json["service"]!.ToString().Should().Be("SalesInventory.Api");
    }
}

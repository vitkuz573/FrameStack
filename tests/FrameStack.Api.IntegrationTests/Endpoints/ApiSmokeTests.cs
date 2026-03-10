using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FrameStack.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FrameStack.Api.IntegrationTests.Endpoints;

public sealed class ApiSmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task HealthShouldReturnOk()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisterImageThenCreateSessionShouldReturnCreated()
    {
        using var client = factory.CreateClient();

        var registerResponse = await client.PostAsJsonAsync("/api/v1/images", new
        {
            vendor = "Cisco",
            platform = "Router",
            name = "IOS-XE 17.9",
            version = "17.9.4a",
            artifactPath = "/tmp/cisco-iosxe.bin"
        });

        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var registerJson = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = registerJson.GetProperty("id").GetGuid();

        var createSessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            imageId,
            cpuCores = 2,
            memoryMb = 2048
        });

        Assert.Equal(HttpStatusCode.Created, createSessionResponse.StatusCode);
    }

    [Fact]
    public async Task StartSessionWithoutLocalArtifactShouldReturnNotFound()
    {
        using var client = factory.CreateClient();

        var registerResponse = await client.PostAsJsonAsync("/api/v1/images", new
        {
            vendor = "Cisco",
            platform = "Router",
            name = "IOS-XE 17.9",
            version = "17.9.4a",
            artifactPath = "/tmp/does-not-exist.bin"
        });

        var registerJson = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = registerJson.GetProperty("id").GetGuid();

        var createSessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            imageId,
            cpuCores = 2,
            memoryMb = 2048
        });

        var createJson = await createSessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = createJson.GetProperty("id").GetGuid();

        var startResponse = await client.PostAsync($"/api/v1/sessions/{sessionId}/start", null);

        Assert.Equal(HttpStatusCode.NotFound, startResponse.StatusCode);
    }
}

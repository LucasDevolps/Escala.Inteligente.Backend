using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ScheduleManager.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class ApiAuthorizationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Protected_endpoint_returns_problem_details_when_unauthenticated()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", fixture.ConnectionString);
            builder.UseSetting("Jwt:Issuer", "ScheduleManager.Tests");
            builder.UseSetting("Jwt:Audience", "ScheduleManager.Tests");
            builder.UseSetting("Jwt:SigningKeyBase64", key);
            builder.UseSetting("Encryption:KeyId", "test-key");
            builder.UseSetting("Encryption:KeyBase64", key);
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Bootstrap:Enabled", "false");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await client.GetAsync("/api/v1/employees");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("SESSION_REVOKED", json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
    }
}

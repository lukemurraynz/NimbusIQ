using Atlas.ControlPlane.Api;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.ControlPlane.Tests.Contract;

public sealed class ContractTestFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"atlas_contract_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Replace PostgreSQL DbContext with in-memory provider for deterministic contract tests.
            services.RemoveAll<AtlasDbContext>();
            services.RemoveAll<DbContextOptions<AtlasDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<AtlasDbContext>>();
            // Also replace the DbContextFactory registered in Program.cs for background services.
            services.RemoveAll<IDbContextFactory<AtlasDbContext>>();
            services.AddDbContext<AtlasDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.AddDbContextFactory<AtlasDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName), ServiceLifetime.Scoped);

            // Replace Entra ID auth with a test scheme that always authenticates.
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }
}

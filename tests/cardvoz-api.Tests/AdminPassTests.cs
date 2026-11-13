using System.Security.Cryptography;
using cardvoz_api.Auth;
using cardvoz_api.Models;
using cardvoz_api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace cardvoz_api.Tests;

public class AdminPassTests
{
    private static readonly TokenService Tokens = new(new JwtSettings
    {
        SigningKey = RandomNumberGenerator.GetBytes(48),
        Issuer = "cardvoz-api",
        Audience = "cardvoz"
    });

    [Fact]
    public void Passe_vale_uma_vez_so()
    {
        var passes = new AdminPassService();
        var userId = Guid.NewGuid();
        var (code, _) = passes.Create(userId);

        Assert.Equal(userId, passes.Redeem(code));
        Assert.Null(passes.Redeem(code));
        Assert.Null(passes.Redeem("codigo-inventado"));
        Assert.Null(passes.Redeem(""));
    }

    [Fact]
    public void Passe_vence_depois_de_um_minuto()
    {
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var passes = new AdminPassService(() => now);
        var (code, expiresAt) = passes.Create(Guid.NewGuid());
        Assert.Equal(now.AddMinutes(1), expiresAt);

        now = now.AddSeconds(61);
        Assert.Null(passes.Redeem(code));
    }

    [Fact]
    public async Task Passe_so_abre_sessao_para_quem_ainda_e_admin()
    {
        using var db = TestDb.Create();
        var admin = TestDb.AddUser(db, "admin@exemplo.com", isAdmin: true);
        var comum = TestDb.AddUser(db, "ana@exemplo.com");
        var auth = new AuthService(db, new PasswordHasher<User>(), Tokens, NullLogger<AuthService>.Instance);

        var response = await auth.SignInAdminAsync(admin.Id);
        Assert.NotNull(response);
        Assert.True(response!.User.IsAdmin);
        Assert.False(string.IsNullOrEmpty(response.RefreshToken));

        Assert.Null(await auth.SignInAdminAsync(comum.Id));

        admin.IsAdmin = false;
        db.SaveChanges();
        Assert.Null(await auth.SignInAdminAsync(admin.Id));
    }
}

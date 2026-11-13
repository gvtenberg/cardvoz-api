using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace cardvoz_api.Services;

/// <summary>
/// Passe do painel: o site do CardVoz pede um código de uso único, que vale 1 minuto,
/// e abre o painel (cardvoz-admin) com ele. O painel troca o código por uma sessão
/// própria, sem pedir login de novo. São sites em endereços diferentes, que não
/// enxergam a sessão um do outro.
/// </summary>
/// <remarks>
/// Fica só na memória da API (singleton), sem tabela no banco: se a API reiniciar
/// nesse minuto, o passe se perde e o painel mostra o login normal. Guarda o hash
/// do código, como os tokens de renovação.
/// </remarks>
public class AdminPassService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (Guid UserId, DateTime ExpiresAt)> _passes = new();
    private readonly Func<DateTime> _now;

    public AdminPassService() : this(() => DateTime.UtcNow) { }

    /// <summary>Relógio trocável para os testes.</summary>
    public AdminPassService(Func<DateTime> now)
    {
        _now = now;
    }

    public (string Code, DateTime ExpiresAt) Create(Guid userId)
    {
        var now = _now();
        // Faxina: passes que ninguém usou não ficam acumulando
        foreach (var (hash, pass) in _passes)
        {
            if (pass.ExpiresAt <= now) _passes.TryRemove(hash, out _);
        }

        // 256 bits de um gerador criptográfico, como o token de renovação
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now.Add(Lifetime);
        _passes[Hash(code)] = (userId, expiresAt);
        return (code, expiresAt);
    }

    /// <summary>
    /// Devolve a conta do passe e o apaga (uso único, mesmo com dois pedidos ao mesmo
    /// tempo: só um consegue removê-lo). Null se o código não existe, já foi usado ou venceu.
    /// </summary>
    public Guid? Redeem(string code)
    {
        if (string.IsNullOrEmpty(code) || !_passes.TryRemove(Hash(code), out var pass)) return null;
        return pass.ExpiresAt > _now() ? pass.UserId : null;
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}

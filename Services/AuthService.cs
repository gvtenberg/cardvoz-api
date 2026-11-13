using cardvoz_api.Auth;
using cardvoz_api.Data;
using cardvoz_api.DTOs.Auth;
using cardvoz_api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace cardvoz_api.Services;

public class AuthService : IAuthService
{
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Dois pedidos quase simultâneos com o mesmo token (ex.: duas abas) não são
    /// tratados como roubo dentro desta janela.
    /// </summary>
    private static readonly TimeSpan RefreshReuseGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hash de uma senha aleatória, usado quando o e-mail não existe: a verificação
    /// leva o mesmo tempo, e o tempo de resposta não revela quem tem conta.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<User>().HashPassword(new User(), Guid.NewGuid().ToString());

    private readonly CardVozDbContext _context;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        CardVozDbContext context,
        IPasswordHasher<User> passwordHasher,
        ITokenService tokenService,
        ILogger<AuthService> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<AuthResult> RegisterAsync(RegisterDto dto)
    {
        var email = NormalizeEmail(dto.Email);
        if (await _context.Users.AnyAsync(u => u.Email == email))
        {
            return AuthResult.Fail(AuthFailure.EmailAlreadyRegistered);
        }

        var user = new User
        {
            Name = dto.Name.Trim(),
            Email = email,
            CreatedAt = DateTime.UtcNow,
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, dto.Password);
        _context.Users.Add(user);

        try
        {
            var result = await IssueTokensAsync(user);
            _logger.LogInformation("Usuário cadastrado: {UserId}", user.Id);
            return result;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Outro cadastro com o mesmo e-mail chegou primeiro
            return AuthResult.Fail(AuthFailure.EmailAlreadyRegistered);
        }
    }

    public async Task<AuthResult> LoginAsync(LoginDto dto)
    {
        var email = NormalizeEmail(dto.Email);
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
        // Conta criada pelo Google não tem senha: responde igual a e-mail inexistente
        if (user?.PasswordHash == null)
        {
            _passwordHasher.VerifyHashedPassword(new User(), DummyPasswordHash, dto.Password);
            return AuthResult.Fail(AuthFailure.InvalidCredentials);
        }

        var now = DateTime.UtcNow;
        if (user.LockoutEnd > now)
        {
            return AuthResult.Fail(AuthFailure.LockedOut, user.LockoutEnd);
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, dto.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.FailedLoginCount = 0;
                user.LockoutEnd = now.Add(LockoutDuration);
                _logger.LogWarning("Login bloqueado após tentativas erradas: {UserId}", user.Id);
            }

            await _context.SaveChangesAsync();
            return user.LockoutEnd > now
                ? AuthResult.Fail(AuthFailure.LockedOut, user.LockoutEnd)
                : AuthResult.Fail(AuthFailure.InvalidCredentials);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // O padrão de hash ficou mais forte desde o cadastro: atualiza
            user.PasswordHash = _passwordHasher.HashPassword(user, dto.Password);
        }

        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        return await IssueTokensAsync(user);
    }

    public async Task<AuthResult> LoginWithGoogleAsync(GoogleIdentity identity)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.GoogleSubject == identity.Subject);
        if (user == null)
        {
            // Sem e-mail confirmado, não dá para garantir que o e-mail é da pessoa
            if (!identity.EmailVerified)
            {
                return AuthResult.Fail(AuthFailure.GoogleEmailNotVerified);
            }

            var email = NormalizeEmail(identity.Email);
            user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user == null)
            {
                user = new User
                {
                    Name = BuildName(identity.Name, email),
                    Email = email,
                    GoogleSubject = identity.Subject,
                    CreatedAt = DateTime.UtcNow,
                    // O Google já confirmou o e-mail
                    EmailConfirmedAt = DateTime.UtcNow,
                };
                _context.Users.Add(user);
                _logger.LogInformation("Usuário cadastrado pelo Google: {UserId}", user.Id);
            }
            else if (user.GoogleSubject == null && user.EmailConfirmedAt != null)
            {
                // Conta com senha e e-mail confirmado: a mesma pessoa. Liga o Google a ela,
                // e a pessoa passa a entrar dos dois jeitos na mesma conta
                user.GoogleSubject = identity.Subject;
                _logger.LogInformation("Google ligado à conta com senha de e-mail confirmado: {UserId}", user.Id);
            }
            else if (user.GoogleSubject == null)
            {
                // E-mail não confirmado: a conta com senha pode ter sido criada por outra
                // pessoa, que continuaria entrando com a senha dela na conta de quem entrou
                // pelo Google. Só junta depois de confirmar o e-mail
                return AuthResult.Fail(AuthFailure.GoogleEmailHasPasswordAccount);
            }
            else
            {
                // O e-mail já pertence a outra conta Google (ex.: e-mail trocado entre contas)
                return AuthResult.Fail(AuthFailure.GoogleEmailLinkedToOtherAccount);
            }
        }

        try
        {
            return await IssueTokensAsync(user);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Outro pedido criou a conta ao mesmo tempo
            return AuthResult.Fail(AuthFailure.GoogleEmailLinkedToOtherAccount);
        }
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken)
    {
        var hash = _tokenService.HashRefreshToken(refreshToken);
        var stored = await _context.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (stored?.User == null)
        {
            return AuthResult.Fail(AuthFailure.InvalidRefreshToken);
        }

        var now = DateTime.UtcNow;
        if (stored.RevokedAt.HasValue)
        {
            // Um token já trocado voltou a ser usado: sinal de que foi copiado.
            // Encerra todas as sessões da pessoa por segurança.
            if (now - stored.RevokedAt.Value > RefreshReuseGracePeriod)
            {
                await RevokeAllAsync(stored.UserId, now);
                _logger.LogWarning("Reuso de token de renovação; sessões encerradas: {UserId}", stored.UserId);
            }

            return AuthResult.Fail(AuthFailure.InvalidRefreshToken);
        }

        if (stored.ExpiresAt <= now)
        {
            return AuthResult.Fail(AuthFailure.InvalidRefreshToken);
        }

        // Revogar o antigo e criar o novo na mesma transação: se o banco falhar no
        // meio, nada muda e a pessoa pode tentar de novo com o mesmo token. Separados,
        // o antigo ficava revogado sem existir o novo, e a próxima tentativa contava
        // como reuso (encerrando todas as sessões da pessoa)
        await using var transaction = await _context.Database.BeginTransactionAsync();

        // Revoga de forma atômica: se dois pedidos usarem o mesmo token ao mesmo
        // tempo, só um consegue a troca.
        var revoked = await _context.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
        if (revoked == 0)
        {
            return AuthResult.Fail(AuthFailure.InvalidRefreshToken);
        }

        stored.RevokedAt = now;
        var result = await IssueTokensAsync(stored.User, replacing: stored);
        await transaction.CommitAsync();
        return result;
    }

    public async Task LogoutAsync(string refreshToken)
    {
        var hash = _tokenService.HashRefreshToken(refreshToken);
        await _context.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
    }

    public async Task<AuthResponseDto?> SignInAdminAsync(Guid userId)
    {
        // Confere de novo no banco: quem perdeu o admin (ou excluiu a conta) depois de
        // pedir o passe não entra no painel
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsAdmin);
        if (user == null) return null;
        return (await IssueTokensAsync(user)).Response;
    }

    public async Task<UserResponseDto?> GetProfileAsync(Guid userId)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        return user == null ? null : MapUser(user);
    }

    private async Task<AuthResult> IssueTokensAsync(User user, RefreshToken? replacing = null)
    {
        var now = DateTime.UtcNow;
        var (refreshToken, refreshHash) = _tokenService.CreateRefreshToken();
        var stored = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(JwtSettings.RefreshTokenDays),
        };
        _context.RefreshTokens.Add(stored);

        if (replacing != null)
        {
            replacing.ReplacedByTokenId = stored.Id;
        }

        // Faxina: tokens vencidos desta pessoa não servem mais nem para detectar reuso
        await _context.RefreshTokens
            .Where(t => t.UserId == user.Id && t.ExpiresAt < now)
            .ExecuteDeleteAsync();

        await _context.SaveChangesAsync();

        var (accessToken, accessExpiresAt) = _tokenService.CreateAccessToken(user);
        return AuthResult.Ok(new AuthResponseDto
        {
            AccessToken = accessToken,
            AccessTokenExpiresAt = accessExpiresAt,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = stored.ExpiresAt,
            User = MapUser(user),
        });
    }

    private async Task RevokeAllAsync(Guid userId, DateTime now)
    {
        await _context.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Nome da conta Google ou, sem ele, a parte do e-mail antes do @.</summary>
    private static string BuildName(string? googleName, string email)
    {
        var name = string.IsNullOrWhiteSpace(googleName) ? email.Split('@')[0] : googleName.Trim();
        return name.Length > 100 ? name[..100] : name;
    }

    private static UserResponseDto MapUser(User user) => new()
    {
        Id = user.Id,
        Name = user.Name,
        Email = user.Email,
        HasPassword = user.PasswordHash != null,
        EmailConfirmed = user.EmailConfirmedAt != null,
        IsAdmin = user.IsAdmin,
        CreatedAt = user.CreatedAt,
    };
}

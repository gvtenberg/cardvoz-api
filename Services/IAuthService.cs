using cardvoz_api.DTOs.Auth;

namespace cardvoz_api.Services;

public enum AuthFailure
{
    EmailAlreadyRegistered,
    InvalidCredentials,
    LockedOut,
    InvalidRefreshToken,
    GoogleEmailNotVerified,
    GoogleEmailLinkedToOtherAccount,
    GoogleEmailHasPasswordAccount,
}

/// <summary>
/// Resultado de cadastro, login (senha ou Google) ou renovação: os tokens ou o motivo da falha.
/// </summary>
public sealed record AuthResult(AuthResponseDto? Response, AuthFailure? Failure = null, DateTime? LockoutEnd = null)
{
    public static AuthResult Ok(AuthResponseDto response) => new(response);
    public static AuthResult Fail(AuthFailure failure, DateTime? lockoutEnd = null) => new(null, failure, lockoutEnd);
}

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterDto dto);
    Task<AuthResult> LoginAsync(LoginDto dto);
    Task<AuthResult> LoginWithGoogleAsync(GoogleIdentity identity);
    Task<AuthResult> RefreshAsync(string refreshToken);
    Task LogoutAsync(string refreshToken);
    /// <summary>Nova sessão para uma conta que ainda é admin (passe do painel); null se não for mais.</summary>
    Task<AuthResponseDto?> SignInAdminAsync(Guid userId);
    Task<UserResponseDto?> GetProfileAsync(Guid userId);
}

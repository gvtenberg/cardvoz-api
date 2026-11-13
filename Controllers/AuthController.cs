using cardvoz_api.Auth;
using cardvoz_api.DTOs.Auth;
using cardvoz_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace cardvoz_api.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IGoogleAuthService _googleAuthService;
    private readonly GoogleSettings _googleSettings;

    public AuthController(IAuthService authService, IGoogleAuthService googleAuthService, GoogleSettings googleSettings)
    {
        _authService = authService;
        _googleAuthService = googleAuthService;
        _googleSettings = googleSettings;
    }

    /// <summary>
    /// Cria uma conta e já devolve os tokens de acesso e de renovação.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponseDto>> Register(
        [FromBody] RegisterDto dto, [FromServices] EmailConfirmationService confirmations)
    {
        var result = await _authService.RegisterAsync(dto);
        if (result.Response == null)
        {
            return Conflict(new { message = "Já existe uma conta com este e-mail." });
        }

        // Link de confirmação: falhar no envio não desfaz o cadastro (dá para pedir de novo no Perfil)
        try
        {
            await confirmations.SendAsync(result.Response.User.Id);
        }
        catch (Exception ex)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>()
                .LogError(ex, "Não foi possível enviar a confirmação de e-mail da conta {UserId}.", result.Response.User.Id);
        }

        return CreatedAtAction(nameof(Me), null, result.Response);
    }

    /// <summary>Confirma o e-mail com o código do link (uso único, 24 horas).</summary>
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailDto dto, [FromServices] EmailConfirmationService confirmations)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        return await confirmations.ConfirmAsync(dto.Token)
            ? NoContent()
            : BadRequest(new { message = "Este link não vale mais: ele já foi usado ou passou de 24 horas. Peça um novo no Perfil." });
    }

    /// <summary>Manda de novo o link de confirmação para o e-mail da pessoa logada.</summary>
    [HttpPost("resend-confirmation")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResendConfirmation([FromServices] EmailConfirmationService confirmations)
    {
        return await confirmations.SendAsync(User.GetUserId()) switch
        {
            SendConfirmationResult.Sent => Ok(new { message = "Enviamos o link de confirmação para o seu e-mail. Confira a caixa de entrada e o spam." }),
            SendConfirmationResult.AlreadyConfirmed => Conflict(new { message = "O seu e-mail já está confirmado." }),
            SendConfirmationResult.TooSoon => StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Já enviamos um link há menos de 1 minuto. Confira o seu e-mail ou espere um pouco para pedir de novo." }),
            SendConfirmationResult.NotConfigured => StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "O envio de e-mail ainda não está configurado. Peça ajuda à equipe do CardVoz." }),
            _ => NotFound(new { message = "Conta não encontrada." })
        };
    }

    /// <summary>
    /// Entra com e-mail e senha. Após 5 senhas erradas seguidas, a conta fica bloqueada por 15 minutos.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponseDto>> Login([FromBody] LoginDto dto)
    {
        var result = await _authService.LoginAsync(dto);
        if (result.Failure == AuthFailure.LockedOut && result.LockoutEnd.HasValue)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((result.LockoutEnd.Value - DateTime.UtcNow).TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"Muitas tentativas de login. Tente de novo em {Math.Ceiling(seconds / 60.0)} minuto(s).",
            });
        }

        if (result.Response == null)
        {
            // Mesma mensagem para e-mail inexistente e senha errada
            return Unauthorized(new { message = "E-mail ou senha incorretos." });
        }

        return Ok(result.Response);
    }

    /// <summary>
    /// Entra com a conta Google. Na primeira vez, cria a conta no CardVoz. Se o e-mail já
    /// tiver uma conta criada com senha, responde 409 e a pessoa entra com a senha.
    /// </summary>
    /// <remarks>
    /// O site manda a pessoa ao Google com PKCE e recebe de volta, em /entrar/, um código
    /// de uso único. Esse código, o verificador PKCE e o mesmo endereço de retorno vêm para cá.
    /// </remarks>
    [HttpPost("google")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Session)]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AuthResponseDto>> GoogleLogin([FromBody] GoogleLoginDto dto, CancellationToken cancellationToken)
    {
        if (!_googleSettings.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "O login com Google ainda não foi configurado na API.",
            });
        }

        if (!_googleSettings.IsAllowedRedirectUri(dto.RedirectUri))
        {
            return BadRequest(new { message = "Endereço de retorno não permitido." });
        }

        var identity = await _googleAuthService.ExchangeCodeAsync(dto.Code, dto.CodeVerifier, dto.RedirectUri, cancellationToken);
        if (identity == null)
        {
            return Unauthorized(new { message = "Não foi possível entrar com o Google. Tente de novo." });
        }

        var result = await _authService.LoginWithGoogleAsync(identity);
        return result.Failure switch
        {
            AuthFailure.GoogleEmailNotVerified => Unauthorized(new
            {
                message = "O e-mail desta conta Google não foi confirmado. Confirme o e-mail no Google e tente de novo.",
            }),
            AuthFailure.GoogleEmailLinkedToOtherAccount => Conflict(new
            {
                message = "Este e-mail já está ligado a outra conta Google no CardVoz.",
            }),
            AuthFailure.GoogleEmailHasPasswordAccount => Conflict(new
            {
                message = "Este e-mail já tem uma conta no CardVoz criada com senha. Entre com seu e-mail e sua senha e, no Perfil, confirme o e-mail: depois disso, o Google também entra nessa conta.",
            }),
            _ => Ok(result.Response),
        };
    }

    /// <summary>
    /// "Esqueci minha senha": manda um link por e-mail se o e-mail tiver conta. A
    /// resposta é sempre a mesma, para a rota não revelar quais e-mails têm conta.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto, [FromServices] PasswordResetService resets)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (!resets.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "O envio de e-mail ainda não está configurado. Peça ajuda à equipe do CardVoz." });

        await resets.RequestAsync(dto.Email);
        return Ok(new { message = "Se este e-mail tiver conta no CardVoz, enviamos um link para criar uma nova senha. Confira a caixa de entrada e o spam." });
    }

    /// <summary>Troca a senha com o código do link (uso único, 1 hora) e encerra as sessões abertas.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto, [FromServices] PasswordResetService resets)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        return await resets.ResetAsync(dto.Token, dto.Password)
            ? NoContent()
            : BadRequest(new { message = "Este link não vale mais: ele já foi usado ou passou de 1 hora. Peça um novo em \"Esqueci minha senha\"." });
    }

    /// <summary>
    /// Troca um token de renovação válido por um novo par de tokens. O token usado deixa de valer.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Session)]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponseDto>> Refresh([FromBody] RefreshTokenDto dto)
    {
        var result = await _authService.RefreshAsync(dto.RefreshToken);
        if (result.Response == null)
        {
            return Unauthorized(new { message = "Sessão expirada. Entre novamente." });
        }

        return Ok(result.Response);
    }

    /// <summary>
    /// Encerra a sessão revogando o token de renovação. Sempre responde 204.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Session)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenDto dto)
    {
        await _authService.LogoutAsync(dto.RefreshToken);
        return NoContent();
    }

    /// <summary>
    /// Passe do painel: código de uso único (1 minuto) para abrir o cardvoz-admin já
    /// logado. Só para admins. O site o põe depois do # do endereço do painel.
    /// </summary>
    [HttpPost("admin-pass")]
    [Authorize]
    [AdminOnly]
    [EnableRateLimiting(RateLimitPolicies.Session)]
    [ProducesResponseType(typeof(AdminPassResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<AdminPassResponseDto> CreateAdminPass([FromServices] AdminPassService passes)
    {
        var (code, expiresAt) = passes.Create(User.GetUserId());
        return Ok(new AdminPassResponseDto { Code = code, ExpiresAt = expiresAt });
    }

    /// <summary>Troca o passe do painel por uma sessão nova (tokens próprios do painel).</summary>
    [HttpPost("admin-pass/redeem")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthResponseDto>> RedeemAdminPass(
        [FromBody] RedeemAdminPassDto dto, [FromServices] AdminPassService passes)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = passes.Redeem(dto.Code);
        var response = userId == null ? null : await _authService.SignInAdminAsync(userId.Value);
        return response == null
            ? BadRequest(new { message = "Este acesso ao painel não vale mais. Entre com a sua conta." })
            : Ok(response);
    }

    /// <summary>
    /// Dados da pessoa logada.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserResponseDto>> Me()
    {
        var profile = await _authService.GetProfileAsync(User.GetUserId());
        if (profile == null)
        {
            // Conta excluída com token ainda válido
            return Unauthorized(new { message = "Sessão inválida. Entre novamente." });
        }

        return Ok(profile);
    }
}

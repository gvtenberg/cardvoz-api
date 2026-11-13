using System.ComponentModel.DataAnnotations;

namespace cardvoz_api.DTOs.Auth;

/// <summary>Passe do painel: código de uso único que vale 1 minuto.</summary>
public class AdminPassResponseDto
{
    public string Code { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class RedeemAdminPassDto
{
    [Required(ErrorMessage = "O código do passe é obrigatório.")]
    [MaxLength(100)]
    public string Code { get; set; } = string.Empty;
}

using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using cardvoz_api.Auth;
using cardvoz_api.Data;
using cardvoz_api.Models;
using cardvoz_api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// Carrega variáveis do arquivo .env (se existir) para o ambiente ANTES de montar
// a configuração do ASP.NET Core.
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

// Validação do contêiner de DI em ambiente de desenvolvimento
builder.Host.UseDefaultServiceProvider((context, options) =>
{
    options.ValidateOnBuild = context.HostingEnvironment.IsDevelopment();
    options.ValidateScopes = context.HostingEnvironment.IsDevelopment();
});

// === CONFIGURAÇÃO DE CONTROLLERS E SERIALIZAÇÃO JSON ===
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

// === CONFIGURAÇÃO DO SWAGGER/OPENAPI ===
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CardVoz API",
        Version = "v1",
        Description = "API RESTful acessível para estudo com flashcards e comandos de voz do CardVoz.",
        Contact = new OpenApiContact
        {
            Name = "Equipe CardVoz",
            Url = new Uri("https://github.com/gvtenberg/cardvoz")
        }
    });

    // Botão "Authorize" do Swagger: cole o accessToken retornado pelo login
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Token de acesso retornado por /api/auth/google, /api/auth/login ou /api/auth/register.",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            },
            Array.Empty<string>()
        },
    });

    var xmlFilename = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

// === CONFIGURAÇÃO DE CORS ===
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[]
    {
        "http://localhost:3000",
        "http://localhost:3001",
        "http://localhost:3002",
        "http://127.0.0.1:3000"
    };

var envCorsOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
if (!string.IsNullOrWhiteSpace(envCorsOrigins))
{
    var extra = envCorsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    configuredOrigins = configuredOrigins.Concat(extra).Distinct().ToArray();
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(configuredOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            // O cabeçalho Authorization faz o navegador perguntar (OPTIONS) antes de cada
            // chamada; guardar a resposta evita uma ida extra à API por requisição.
            .SetPreflightMaxAge(TimeSpan.FromHours(2));
    });
});

// === CONFIGURAÇÃO DO BANCO DE DADOS (PostgreSQL / Supabase) ===
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Defina a conexão do Supabase na variável ConnectionStrings__DefaultConnection " +
        "(ex.: postgresql://postgres.<projeto>:<senha>@aws-0-<regiao>.pooler.supabase.com:5432/postgres).");
}

builder.Services.AddDbContext<CardVozDbContext>(options =>
    options.UseNpgsql(PostgresSupport.NormalizeConnectionString(connectionString)));

// === AUTENTICAÇÃO (JWT) ===
var jwtSettings = JwtSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(jwtSettings);

// Login com Google (opcional: sem GOOGLE_CLIENT_ID e GOOGLE_CLIENT_SECRET, a rota responde 503)
var googleSettings = GoogleSettings.FromConfiguration(builder.Configuration, configuredOrigins);
builder.Services.AddSingleton(googleSettings);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Mantém os nomes originais dos claims ("sub" em vez do formato longo da Microsoft)
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtSettings.SigningKey),
            // Só aceita o algoritmo usado na emissão (impede trocar o algoritmo no token)
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    });

// Seguro por padrão: toda rota exige login, exceto as marcadas com [AllowAnonymous]
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// === LIMITE DE TENTATIVAS (cadastro, login, Google, renovação) ===
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy(RateLimitPolicies.Session, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Muitas tentativas. Aguarde um minuto e tente de novo." }, cancellationToken);
    };
});

// O Render fica na frente da API como proxy: o IP real do cliente vem no
// X-Forwarded-For (usado pelo limite de tentativas acima)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// === INJEÇÃO DE DEPENDÊNCIA (SERVICES) ===
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
// Singleton: os passes do painel ficam na memória entre um pedido e outro
builder.Services.AddSingleton<AdminPassService>();
builder.Services.AddHttpClient<IGoogleAuthService, GoogleAuthService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IDeckService, DeckService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<ICardService, CardService>();
builder.Services.AddScoped<IStudyService, StudyService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddSingleton(EmailSettings.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<EmailSender>();
builder.Services.AddScoped<PasswordResetService>();
builder.Services.AddScoped<EmailConfirmationService>();

var app = builder.Build();

// === VERIFICAÇÃO DO BANCO DE DADOS ===
// O esquema e os dados iniciais vêm das migrations do repositório cardvoz-db.
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var context = services.GetRequiredService<CardVozDbContext>();
        var deckCount = context.Decks.Count();
        logger.LogInformation("Banco de dados conectado ({DeckCount} baralhos).", deckCount);
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Não foi possível consultar o banco. Confira a conexão do Supabase e se as " +
            "migrations do repositório cardvoz-db foram aplicadas (supabase db push).");
    }
}

// === PIPELINE HTTP ===
app.UseForwardedHeaders();
app.UseCors("AllowFrontend");

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "CardVoz API v1");
    c.RoutePrefix = "swagger";
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Redireciona a raiz para o Swagger UI
app.MapGet("/", () => Results.Redirect("/swagger")).AllowAnonymous();

app.MapControllers();

app.Run();

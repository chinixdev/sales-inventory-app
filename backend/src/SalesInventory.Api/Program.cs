using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using SalesInventory.Api.Middleware;
using SalesInventory.Application;
using SalesInventory.Infrastructure;
using SalesInventory.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// Configuración de capas (Clean Architecture & Dependency Inversion)
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<DevelopmentAdminSeeder>();
}

// Servicios de controladores y API
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? new JwtOptions();
jwtOptions.Validate();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwtOptions.CreateSigningKey(),
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

// OpenAPI 
builder.Services.AddOpenApi();

// Configuración CORS para el cliente Angular
const string corsPolicyName = "AllowAngularClient";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
                     ?? ["http://localhost:4200"];

builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicyName, policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await app.Services.GetRequiredService<DevelopmentAdminSeeder>().SeedAsync();
}

// Middleware de manejo global de excepciones y respuestas HTTP consistentes
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Pipeline HTTP
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("SalesInventory API Reference")
               .WithTheme(ScalarTheme.Moon);
    });
}

app.UseHttpsRedirection();

app.UseCors(corsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

// Endpoint nativo de Health Check
app.MapHealthChecks("/health");

// Controladores de la API
app.MapControllers();

app.Run();

// Expone la clase Program para pruebas de integración con WebApplicationFactory
public partial class Program { }

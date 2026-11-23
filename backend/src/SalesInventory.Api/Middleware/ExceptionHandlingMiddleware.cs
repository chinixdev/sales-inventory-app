using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Domain.Exceptions;

namespace SalesInventory.Api.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, problemDetails) = exception switch
        {
            NotFoundException notFound => (
                HttpStatusCode.NotFound,
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Recurso no encontrado",
                    Detail = notFound.Message,
                    Instance = context.Request.Path
                }
            ),
            ConflictException conflict => (
                HttpStatusCode.Conflict,
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflicto de negocio",
                    Detail = conflict.Message,
                    Instance = context.Request.Path
                }
            ),
            DbUpdateConcurrencyException => (
                HttpStatusCode.Conflict,
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflicto de concurrencia optimista",
                    Detail = "El recurso fue modificado o eliminado por otra transacción concurrente. Por favor recargue los datos y vuelva a intentarlo.",
                    Instance = context.Request.Path
                }
            ),
            InsufficientStockException insufficientStock => (
                HttpStatusCode.BadRequest,
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Stock insuficiente",
                    Detail = insufficientStock.Message,
                    Instance = context.Request.Path
                }
            ),
            DomainException domain => (
                HttpStatusCode.BadRequest,
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Regla de dominio violada",
                    Detail = domain.Message,
                    Instance = context.Request.Path
                }
            ),
            ValidationException validation => (
                HttpStatusCode.BadRequest,
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Error de validación",
                    Detail = validation.Message,
                    Instance = context.Request.Path,
                    Extensions = { ["errors"] = validation.Errors }
                }
            ),
            _ => (
                HttpStatusCode.InternalServerError,
                new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Error interno del servidor",
                    Detail = "Ocurrió un error inesperado al procesar la solicitud.",
                    Instance = context.Request.Path
                }
            )
        };

        if (statusCode == HttpStatusCode.InternalServerError)
        {
            _logger.LogError(exception, "Excepción no controlada en la ruta {Path}", context.Request.Path);
        }
        else
        {
            _logger.LogWarning("Excepción de negocio ({StatusCode}): {Message} en {Path}", statusCode, exception.Message, context.Request.Path);
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = (int)statusCode;

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails, jsonOptions));
    }
}

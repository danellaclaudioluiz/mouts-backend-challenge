using Ambev.DeveloperEvaluation.Common.Validation;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.WebApi.Common;
using FluentValidation;
using System.Text.Json;

namespace Ambev.DeveloperEvaluation.WebApi.Middleware
{
    /// <summary>
    /// Translates well-known exceptions raised anywhere in the request
    /// pipeline into the API's standard ApiResponse envelope. Order matters:
    /// the most specific exception types are caught first.
    /// </summary>
    public class ValidationExceptionMiddleware
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly RequestDelegate _next;

        public ValidationExceptionMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (ValidationException ex)
            {
                await WriteAsync(context, StatusCodes.Status400BadRequest, new ApiResponse
                {
                    Success = false,
                    Message = "Validation Failed",
                    Errors = ex.Errors.Select(e => (ValidationErrorDetail)e)
                });
            }
            catch (ResourceNotFoundException ex)
            {
                await WriteAsync(context, StatusCodes.Status404NotFound, new ApiResponse
                {
                    Success = false,
                    Message = ex.Message
                });
            }
            catch (DomainException ex)
            {
                await WriteAsync(context, StatusCodes.Status400BadRequest, new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    Errors = new[] { new ValidationErrorDetail
                    {
                        Error = "DomainRule",
                        Detail = ex.Message
                    }}
                });
            }
            catch (InvalidOperationException ex)
            {
                await WriteAsync(context, StatusCodes.Status409Conflict, new ApiResponse
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        private static Task WriteAsync(HttpContext context, int statusCode, ApiResponse body)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
        }
    }
}

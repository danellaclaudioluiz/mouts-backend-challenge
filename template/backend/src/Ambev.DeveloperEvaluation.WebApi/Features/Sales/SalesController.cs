using Ambev.DeveloperEvaluation.Application.Sales.CancelSale;
using Ambev.DeveloperEvaluation.Application.Sales.CancelSaleItem;
using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using Ambev.DeveloperEvaluation.Application.Sales.DeleteSale;
using Ambev.DeveloperEvaluation.Application.Sales.GetSale;
using Ambev.DeveloperEvaluation.Application.Sales.ListSales;
using Ambev.DeveloperEvaluation.Application.Sales.UpdateSale;
using Ambev.DeveloperEvaluation.WebApi.Common;
using Ambev.DeveloperEvaluation.WebApi.Features.Sales.CreateSale;
using Ambev.DeveloperEvaluation.WebApi.Features.Sales.ListSales;
using Ambev.DeveloperEvaluation.WebApi.Features.Sales.UpdateSale;
using Asp.Versioning;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Sales;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class SalesController : BaseController
{
    private readonly IMediator _mediator;
    private readonly IMapper _mapper;

    public SalesController(IMediator mediator, IMapper mapper)
    {
        _mediator = mediator;
        _mapper = mapper;
    }

    /// <summary>Creates a new sale with all of its items.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponseWithData<SaleDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateSale(
        [FromBody] CreateSaleRequest request,
        CancellationToken cancellationToken)
    {
        var command = _mapper.Map<CreateSaleCommand>(request);
        var result = await _mediator.Send(command, cancellationToken);
        Response.Headers.ETag = ETagFor(result.RowVersion);

        return CreatedAtAction(
            nameof(GetSale),
            new { id = result.Id },
            new ApiResponseWithData<SaleDto>
            {
                Success = true,
                Message = "Sale created successfully",
                Data = result
            });
    }

    /// <summary>Retrieves a sale by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponseWithData<SaleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSale([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSaleQuery(id), cancellationToken);
        Response.Headers.ETag = ETagFor(result.RowVersion);

        // BaseController.Ok<T>(T) wraps the value in ApiResponseWithData<T>;
        // passing an already-wrapped envelope here would double-encode and
        // surface as {"data":{"data":...}} on the wire.
        return Ok(result);
    }

    /// <summary>
    /// Lists sales with pagination, filtering and ordering per the API conventions.
    /// Items are returned as header-only summaries — fetch a specific sale to
    /// see its line items.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResponse<SaleSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListSales([FromQuery] ListSalesRequest request, CancellationToken cancellationToken)
    {
        var query = _mapper.Map<ListSalesQuery>(request);
        var result = await _mediator.Send(query, cancellationToken);

        // ControllerBase.Ok (note the explicit cast — `base.Ok` would pick
        // BaseController's wrapping helper) — PaginatedResponse already
        // carries success/data/pagination fields.
        return ((ControllerBase)this).Ok(new PaginatedResponse<SaleSummaryDto>
        {
            Success = true,
            Data = result.Items,
            CurrentPage = result.Page,
            TotalPages = result.TotalPages,
            TotalCount = result.TotalCount,
            NextCursor = result.NextCursor
        });
    }

    /// <summary>
    /// Replaces the sale identified by id (header + items). If the request
    /// carries an <c>If-Match</c> header, its value is compared to the current
    /// sale's ETag — a mismatch returns 412 Precondition Failed.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponseWithData<SaleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateSale(
        [FromRoute] Guid id,
        [FromBody] UpdateSaleRequest request,
        CancellationToken cancellationToken)
    {
        var command = _mapper.Map<UpdateSaleCommand>(request);
        command.Id = id;
        command.ExpectedRowVersion = ParseIfMatch(Request.Headers.IfMatch);

        var result = await _mediator.Send(command, cancellationToken);
        Response.Headers.ETag = ETagFor(result.RowVersion);

        return Ok(result);
    }

    /// <summary>
    /// Hard-deletes a sale and its items. Honours <c>If-Match</c> the same way
    /// PUT does — a mismatched ETag returns 412.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteSale([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var expectedRowVersion = ParseIfMatch(Request.Headers.IfMatch);
        await _mediator.Send(new DeleteSaleCommand(id, expectedRowVersion), cancellationToken);
        // ApiResponse already carries success/message, no need to wrap again.
        return ((ControllerBase)this).Ok(new ApiResponse { Success = true, Message = "Sale deleted successfully" });
    }

    /// <summary>Soft-cancels a sale (sets IsCancelled = true). Idempotent.</summary>
    [HttpPatch("{id:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponseWithData<SaleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CancelSale([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new CancelSaleCommand(id), cancellationToken);
        Response.Headers.ETag = ETagFor(result.RowVersion);

        return Ok(result);
    }

    /// <summary>Cancels a single item within a sale and recalculates the total.</summary>
    [HttpPatch("{id:guid}/items/{itemId:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponseWithData<SaleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CancelSaleItem(
        [FromRoute] Guid id,
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new CancelSaleItemCommand(id, itemId), cancellationToken);
        Response.Headers.ETag = ETagFor(result.RowVersion);

        return Ok(result);
    }

    /// <summary>Encodes an aggregate's row version as a strong HTTP ETag.</summary>
    private static string ETagFor(long rowVersion) => $"\"{rowVersion:x}\"";

    /// <summary>
    /// Parses an If-Match header into a row version. Returns null if the
    /// header is absent, "*" (any) or unparseable. The handler then skips the
    /// precondition; the caller has explicitly opted out of strict ordering.
    /// </summary>
    private static long? ParseIfMatch(Microsoft.Extensions.Primitives.StringValues header)
    {
        var value = header.ToString();
        if (string.IsNullOrWhiteSpace(value) || value == "*") return null;

        var trimmed = value.Trim().Trim('"');
        return long.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }
}

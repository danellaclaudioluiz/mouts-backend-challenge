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
[Produces("application/json")]
public class SalesController : BaseController
{
    private readonly IMediator _mediator;
    private readonly IMapper _mapper;

    public SalesController(IMediator mediator, IMapper mapper)
    {
        _mediator = mediator;
        _mapper = mapper;
    }

    /// <summary>Create a sale (header + items).</summary>
    /// <remarks>
    /// Idempotent under `Idempotency-Key`: the middleware caches the
    /// first 2xx for 24h and replays it byte-equal on retries with the
    /// same body; a different body under the same key returns **422**.
    /// </remarks>
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

    /// <summary>Get a sale by id.</summary>
    /// <remarks>
    /// Returns the sale with all of its line items. Emits an `ETag`
    /// header (the current `RowVersion`) which the client should echo
    /// back as `If-Match` on subsequent `PUT` / `DELETE` / `PATCH`.
    /// Reads are served from the distributed cache when warm.
    /// </remarks>
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

    /// <summary>List sales (paginated, filterable, orderable).</summary>
    /// <remarks>
    /// Rows are returned as header-only summaries — fetch a specific
    /// sale by id to see its line items. Supports `_page`, `_size` (≤
    /// 100), `_order` (whitelisted columns, comma-separated, with
    /// `asc`/`desc`), and the usual date/customer/branch/status filters.
    /// </remarks>
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

    /// <summary>Replace a sale (header + items).</summary>
    /// <remarks>
    /// Optimistic concurrency: send the ETag returned by the previous
    /// `GET` / `POST` in an `If-Match` header. A mismatch returns
    /// **412 Precondition Failed**. Pass `If-Match: *` to bypass the
    /// check explicitly.
    /// </remarks>
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

    /// <summary>Hard-delete a sale (cascades to its items).</summary>
    /// <remarks>
    /// Honours `If-Match` the same way `PUT` does — a mismatched ETag
    /// returns **412**. Responds with **204 No Content** on success
    /// (no body, no envelope — REST idiom for delete).
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteSale([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var expectedRowVersion = ParseIfMatch(Request.Headers.IfMatch);
        await _mediator.Send(new DeleteSaleCommand(id, expectedRowVersion), cancellationToken);
        // 204 No Content is the REST idiom for a successful delete — no
        // body, no envelope. Switching from 200+envelope removed a layer
        // of "Stripe-style ack" that clients didn't need anyway.
        return NoContent();
    }

    /// <summary>Soft-cancel a sale.</summary>
    /// <remarks>
    /// Sets `IsCancelled = true` and raises a `SaleCancelledEvent` on
    /// the outbox. Idempotent: cancelling an already-cancelled sale
    /// returns **200** without re-emitting the event.
    /// </remarks>
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

    /// <summary>Cancel a single item within a sale.</summary>
    /// <remarks>
    /// Marks the item cancelled, recalculates the sale's total, and
    /// raises an `ItemCancelledEvent`. Idempotent on the item.
    /// </remarks>
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

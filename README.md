# Developer Evaluation Project

`READ CAREFULLY`

## Use Case
**You are a developer on the DeveloperStore team. Now we need to implement the API prototypes.**

As we work with `DDD`, to reference entities from other domains, we use the `External Identities` pattern with denormalization of entity descriptions.

Therefore, you will write an API (complete CRUD) that handles sales records. The API needs to be able to inform:

* Sale number
* Date when the sale was made
* Customer
* Total sale amount
* Branch where the sale was made
* Products
* Quantities
* Unit prices
* Discounts
* Total amount for each item
* Cancelled/Not Cancelled

It's not mandatory, but it would be a differential to build code for publishing events of:
* SaleCreated
* SaleModified
* SaleCancelled
* ItemCancelled

If you write the code, **it's not required** to actually publish to any Message Broker. You can log a message in the application log or however you find most convenient.

### Business Rules

* Purchases above 4 identical items have a 10% discount
* Purchases between 10 and 20 identical items have a 20% discount
* It's not possible to sell above 20 identical items
* Purchases below 4 items cannot have a discount

These business rules define quantity-based discounting tiers and limitations:

1. Discount Tiers:
   - 4+ items: 10% discount
   - 10-20 items: 20% discount

2. Restrictions:
   - Maximum limit: 20 items per product
   - No discounts allowed for quantities below 4 items

## Overview
This section provides a high-level overview of the project and the various skills and competencies it aims to assess for developer candidates. 

See [Overview](/.doc/overview.md)

## Tech Stack
This section lists the key technologies used in the project, including the backend, testing, frontend, and database components. 

See [Tech Stack](/.doc/tech-stack.md)

## Frameworks
This section outlines the frameworks and libraries that are leveraged in the project to enhance development productivity and maintainability. 

See [Frameworks](/.doc/frameworks.md)

<!-- 
## API Structure
This section includes links to the detailed documentation for the different API resources:
- [API General](./docs/general-api.md)
- [Products API](/.doc/products-api.md)
- [Carts API](/.doc/carts-api.md)
- [Users API](/.doc/users-api.md)
- [Auth API](/.doc/auth-api.md)
-->

## Project Structure
This section describes the overall structure and organization of the project files and directories. 

See [Project Structure](/.doc/project-structure.md)

---

## Sales API

The Sales feature is the implementation of the use case described above. It is
a complete CRUD with discount business rules, soft-cancel semantics and a
domain-event publisher. All endpoints are public (no `[Authorize]`).

### Endpoints

| Verb | Route | Description |
|---|---|---|
| `POST` | `/api/sales` | Create a sale (header + items) |
| `GET` | `/api/sales/{id}` | Get a sale by id |
| `GET` | `/api/sales` | List sales (paginated, filtered, ordered) |
| `PUT` | `/api/sales/{id}` | Full-replace update (header + items, `SaleNumber` is immutable) |
| `DELETE` | `/api/sales/{id}` | Hard-delete a sale and its items |
| `PATCH` | `/api/sales/{id}/cancel` | Soft-cancel a sale (idempotent) |
| `PATCH` | `/api/sales/{id}/items/{itemId}/cancel` | Cancel a single line and recalculate the total |

The list endpoint follows the conventions in
[`/.doc/general-api.md`](.doc/general-api.md): `_page`, `_size`, `_order`,
plus `_minDate`/`_maxDate`, `customerId`, `branchId`, `isCancelled`,
`saleNumber` (substring with `*`).

Validation errors return HTTP 400, missing resources return 404, business-rule
violations (`DomainException`) return 400 under `errors[].error = "DomainRule"`,
and conflicts (e.g. duplicate `SaleNumber`) return 409.

### Discount rules

Quantity-based, applied per product across non-cancelled lines. Adding the
same product twice in one sale merges into a single line so the cap can't
be bypassed by splitting orders.

| Quantity (per product) | Discount |
|---|---|
| 1–3 | 0% |
| 4–9 | 10% |
| 10–20 | 20% |
| above 20 | not allowed (HTTP 400) |

### Domain events

The aggregate raises four events that are dispatched after persistence by
`LoggingDomainEventPublisher` (logged as structured Serilog entries — no real
broker required for the challenge):

- `SaleCreatedEvent` — on POST
- `SaleModifiedEvent` — on PUT
- `SaleCancelledEvent` — on PATCH `/cancel`
- `ItemCancelledEvent` — on PATCH `/items/{itemId}/cancel`

### Running locally

The connection string in `appsettings.Development.json` matches the Postgres
service in `docker-compose.yml` out of the box.

```bash
cd template/backend

# 1. start the database (host port 5432:5432 is exposed)
docker compose up -d ambev.developerevaluation.database

# 2. apply migrations
dotnet ef database update \
  --project src/Ambev.DeveloperEvaluation.ORM \
  --startup-project src/Ambev.DeveloperEvaluation.WebApi

# 3. run the API
dotnet run --project src/Ambev.DeveloperEvaluation.WebApi
```

Swagger UI: `https://localhost:8081/swagger`.

### Running the tests

```bash
cd template/backend
dotnet test
```

The unit suite covers the discount policy tiers, the `Sale` aggregate
invariants (per-product cap, idempotent cancel, item recalculation) and the
main handlers (`CreateSale`, `CancelSale`, `CancelSaleItem`).

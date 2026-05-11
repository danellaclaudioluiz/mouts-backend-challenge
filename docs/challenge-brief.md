# Challenge brief

Verbatim copy of the original DeveloperStore evaluation statement. The
template under [template/backend/](../template/backend/) ships with its
own `.doc/` folder (Overview, Tech Stack, Frameworks, Project Structure,
General API conventions) — the link list below points to those.

---

## Use Case

> You are a developer on the DeveloperStore team. Now we need to
> implement the API prototypes.

As we work with `DDD`, to reference entities from other domains, we use
the **External Identities** pattern with denormalization of entity
descriptions.

Therefore, you will write an API (complete CRUD) that handles sales
records. The API needs to be able to inform:

- Sale number
- Date when the sale was made
- Customer
- Total sale amount
- Branch where the sale was made
- Products
- Quantities
- Unit prices
- Discounts
- Total amount for each item
- Cancelled / Not Cancelled

### Optional: publishing events

It's not mandatory, but it would be a differential to build code for
publishing events of:

- `SaleCreated`
- `SaleModified`
- `SaleCancelled`
- `ItemCancelled`

If you write the code, it's **not required** to actually publish to any
Message Broker. You can log a message in the application log or however
you find most convenient.

> **Status in this submission:** implemented as a transactional outbox
> (Postgres). The dispatcher's `DeliverAsync` writes to the structured
> log — swap it for a real broker via `IDomainEventBroker` (see
> [architecture.md](architecture.md#domain-events--transactional-outbox)).

### Business rules

- Purchases above 4 identical items have a 10% discount
- Purchases between 10 and 20 identical items have a 20% discount
- It's not possible to sell above 20 identical items
- Purchases below 4 items cannot have a discount

Discount tiers:

| Quantity (per product) | Discount |
|---|---|
| 1 – 3 | 0% |
| 4 – 9 | 10% |
| 10 – 20 | 20% |
| > 20 | rejected (HTTP 400) |

> Encoded once in [`SaleItemDiscountPolicy`](../template/backend/src/Ambev.DeveloperEvaluation.Domain/Services/SaleItemDiscountPolicy.cs)
> and enforced again by Postgres `CK_SaleItems_Quantity` (defence in
> depth — see [database.md](database.md#check-constraints)).

---

## Template documentation (preserved from the template)

- [Overview](../template/backend/.doc/overview.md) — not present in this
  repo; placeholder kept for completeness
- [Tech Stack](../template/backend/.doc/tech-stack.md) — same
- [Frameworks](../template/backend/.doc/frameworks.md) — same
- [Project Structure](../template/backend/.doc/project-structure.md) — same
- [General API conventions](.doc/general-api.md) — pagination,
  filtering, ordering rules followed by the Sales list endpoint

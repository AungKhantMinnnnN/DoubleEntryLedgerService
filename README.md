# Double-Entry Ledger Service

A backend service that records money movements as balanced journal entries. Every transaction is stored as a set of debit and credit lines that must balance, so the books cannot drift out of balance. The service supports multiple currencies, is safe to retry, and publishes events for downstream systems.

The central design principle: **the database enforces correctness, not just the application code.**

**Stack:** .NET 10, PostgreSQL, Kafka, k6, Docker Compose.

## Table of Contents

- [Features](#features)
- [Architecture](#architecture)
- [Domain Model](#domain-model)
- [Invariants and How They Are Enforced](#invariants-and-how-they-are-enforced)
- [Design Decisions](#design-decisions)
- [API](#api)
- [Getting Started](#getting-started)
- [Testing](#testing)
- [Load Testing](#load-testing)
- [Out of Scope](#out-of-scope)
- [Roadmap](#roadmap)

## Features

- Accounts and immutable journal entries with a database-enforced rule that debits equal credits
- Idempotency keys for safe client retries
- Optimistic concurrency on accounts and balances
- Exact `decimal` money handling with a documented rounding policy
- Multi-currency support with saved FX rates, so historical entries stay reproducible
- Transactional outbox that publishes events to Kafka
- Append-only audit trail
- k6 load tests with published p99 latency and throughput

## Architecture

```
Client
  |
  v
ASP.NET Core API  --->  PostgreSQL
                         - accounts
                         - journal_entries / journal_lines
                         - account_balances (projection)
                         - fx_rates
                         - outbox
                         - audit_log (append-only)
                              |
                              v
                     Outbox publisher (background service)
                       polls with FOR UPDATE SKIP LOCKED
                              |
                              v
                            Kafka  --->  Downstream consumers
```

> TODO: Replace the sketch above with a proper architecture diagram.

## Domain Model

| Entity | Description |
|---|---|
| **Account** | Id, code, type (asset, liability, equity, revenue, expense), currency, status |
| **Journal entry** | Immutable header: id, idempotency key, posted-at, description, reference |
| **Journal line** | Entry id, account id, direction (debit or credit), amount, currency, and for FX lines the rate used |
| **Account balance** | Maintained projection derived from lines, for fast reads |
| **FX rate** | Base, quote, rate, effective-at, source |

**Corrections are never edits or deletes.** To fix a mistake, post a reversing entry.

## Invariants and How They Are Enforced

| Invariant | Enforcement | Verified by |
|---|---|---|
| Debits equal credits per entry | Deferred constraint trigger on `journal_lines`, checked at commit | SQL-level test that inserts an unbalanced entry directly and expects failure |
| A retried request never posts twice | `Idempotency-Key` header, unique index, stored request hash. Replay returns the original response. Same key with a different body returns 409/422 | Concurrent duplicate-request test produces exactly one entry |
| No lost updates on balances | Version column (or `xmin`) as concurrency token, with retry on conflict | Hot-account contention test |
| No floating-point money | `numeric(19,4)` columns, `decimal` in C#, explicit rounding rules | Property-based tests |
| FX history is reproducible | Each line stores original amount, rate, and converted amount | Test that changes rates and re-reads old entries |
| Events are never lost | Outbox row written in the same transaction as the entry | Kill-the-publisher test: no events lost on restart |
| Audit history cannot be altered | UPDATE/DELETE revoked and blocked by triggers, optional hash chain | Tamper-attempt demo |

## Design Decisions

1. **Balancing across currencies.** Each entry balances per currency. FX conversions route through a dedicated FX clearing account.
2. **Balance reads.** A maintained `account_balances` projection is updated in the same transaction as the entry. This is what motivates optimistic concurrency.
3. **Isolation level.** Read Committed plus constraints and version checks. *(Document the reasoning here once confirmed.)*
4. **Event delivery.** At-least-once via the outbox. Consumers deduplicate on event id. Events are keyed by account or entry id for ordering.
5. **Event contract.** `EntryPosted` and `EntryReversed`. *(Link the schema here once defined.)*
6. **Rounding policy.** *(Document the rounding mode and where it applies.)*

## API

| Method | Path | Notes |
|---|---|---|
| POST | `/accounts` | Create an account |
| GET | `/accounts/{id}` | Fetch an account |
| POST | `/journal-entries` | Post an entry. Requires `Idempotency-Key` |
| POST | `/journal-entries/{id}/reverse` | Post a reversing entry |
| GET | `/accounts/{id}/balance` | Current balance |
| GET | `/accounts/{id}/transactions` | Transaction history |
| POST | `/fx-rates` | Record an FX rate |

## Getting Started

**Prerequisites:** Docker, .NET 10 SDK, k6 (for load tests).

```bash
# Start PostgreSQL, Kafka, and the service
docker compose up

# Apply migrations (if not run automatically on startup)
dotnet ef database update --project src/Ledger.Api
```

> TODO: Confirm project paths and commands once the solution structure exists.

## Testing

```bash
dotnet test
```

- **SQL-level tests:** the balance trigger, audit-table protections
- **Integration tests:** idempotency, concurrency, outbox, against a real PostgreSQL instance
- **Property-based tests:** decimal arithmetic and rounding

## Load Testing

Scenarios (k6):

1. **Steady-state posting** across many accounts
2. **Contention** against a small set of hot accounts
3. **Mixed read/write** workload

```bash
k6 run loadtest/post-entries.js
```

### Results

| Scenario | Throughput (entries/s) | p50 | p95 | p99 | Error / conflict rate |
|---|---|---|---|---|---|
| Steady-state | TBD | TBD | TBD | TBD | TBD |
| Contention | TBD | TBD | TBD | TBD | TBD |
| Mixed | TBD | TBD | TBD | TBD | TBD |

**Test conditions:** *(Record hardware, PostgreSQL configuration, dataset size, and k6 settings here. Report results honestly, including the conditions that limit them.)*

## Out of Scope

- Authentication and multi-tenancy
- A user interface
- Reporting and period-close
- Regulatory compliance

## Roadmap

- [ ] Schema, migrations, and balance-check trigger with SQL-level tests
- [ ] Post and read entries with decimal handling
- [ ] Idempotency
- [ ] Optimistic concurrency
- [ ] Multi-currency and FX
- [ ] Outbox, publisher, and Kafka via Docker Compose
- [ ] Append-only audit trail
- [ ] k6 load tests and published results

## License

TBD
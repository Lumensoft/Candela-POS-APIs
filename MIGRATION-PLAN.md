# POS API — migration plan (net48 → .NET 10)

## Goal

Everything the React tablet uses today is served by `Candela.LegacyHost` (.NET Framework
4.8). Move each endpoint into the `.NET 10` `Candela.Api` so that, screen by screen,
the tablet talks only to `Candela.Api` — **with byte-identical behaviour**. The tablet
is never changed.

## What can and cannot move

| Stays on net48 forever | Why |
|---|---|
| `PrintController` + `RdlcInvoicePrinter` | RDLC invoice rendering. There is no RDLC renderer for .NET 10. |
| DAL write wrappers (`/legacy/*`) | `SaleAndReturnDAL.Add()`, `VoidSale()`, `ReceiveSkimCash()` … only run on .NET Framework, and they must run so the SQL log (HO/shop replication), activity log and inventory posting stay correct. |

So `Candela.LegacyHost` **shrinks** to those two things. It does not disappear — that is
the designed end state, not a failure.

Everything else moves.

## Order (small + safe first, money-math last)

| # | Piece | LegacyHost lines | DAL writes | Target module | Risk | Status |
|---|---|---|---|---|---|---|
| 1 | `LoyaltyController` — points balance | 120 | none (read) | CustomerClub | 🟢 lowest — proves the plumbing | ✅ done |
| 2 | `HoldsController` — park / recall sale | 297 | `AddToHold` | Sales | 🟢 small, one write | ✅ done |
| 3 | `CustomersController` — walk-in create | 268 | none (raw SQL write) | CustomerClub | 🟢 | ✅ done |
| 4 | `GiftCardsController` | 520 | none (raw SQL) | CustomerClub | 🟡 | ✅ done |
| 5 | `MastersController` — lookups | 964 | none (read) | Configuration | 🟡 large but read-only | ✅ done |
| 6 | `AuthController` — login / refresh / supervisor | 413 | `SymmetricEncryption` | Security | 🟡 must byte-verify password decryption, else stays on 4.8 | ✅ done |
| 7 | `HardwareController` — drawer kick | 106 | none | Sales | 🟢 (Windows-only — needs the till) | ✅ done |
| 8 | `QuoteController` — pricing / discount / VAT | 2,800 | 3 | Sales | 🔴 the money engine. Golden-master tests mandatory before switch. | ⏸ on net48 (proxy) — needs golden-master run before port |
| 9 | `ReturnsController` | 970 | `IsValidInvoiceForReturn`, `Add` | Sales | 🔴 | ✅ done |
| 10 | `SalesController` — post sale | 1,304 | `dal.Add`, `VoidSale` | Sales | 🔴 the sale itself. H2 / H4 findings verified here. | ✅ done |
| 11 | `PosController` — cash mgmt, shift close | 1,053 | `ReceiveSkimCash`, `UpdateShiftClosing` | Sales | 🔴 | ✅ done |

## Per-step checklist — every piece follows this, no exceptions

1. **Read** the LegacyHost controller in full. Note every route, every response field
   and its exact type, every DAL call, every business message.
2. **Port** into `src/Modules/<Module>/<Slice>/`:
   - `<Slice>Controller.cs` — route + response shape **identical**
   - `I<Slice>Repository.cs` / `<Slice>Repository.cs` — reads via `IDb`; writes via
     `ILegacyHostClient`
   - `I<Slice>Service.cs` / `<Slice>Service.cs` — only if there are rules to name
   - `Dtos/` — request and response, split
   - register in `<Module>Module.cs`
3. **SQL is copied verbatim.** Reformatting an `isnull` or a join changes column names
   or null handling.
4. **DAL writes** get a `/legacy/<slice>` wrapper in `Candela.LegacyHost` that runs the
   DAL exactly as the desktop form does.
5. **Build** — `Candela.Api` and `Candela.LegacyHost` both green, no new warnings.
6. **Parity test** — `Candela.Tests` sends the same request to the old endpoint and the
   new one and diffs the JSON. Must be identical (see "What the diff ignores" below).
7. **Route switch** — the tablet keeps calling the same URL; only which process answers
   changes. Done by hosting config, not a code change in the tablet.
8. **Monitor** — one shift on the new path with the old still deployable as instant
   rollback.

## The parity test (`Candela.Tests`)

`dotnet test` project. For each migrated endpoint:

```
OLD  = http://localhost:<legacy>/api/...
NEW  = http://localhost:<net10>/api/...
```

Same headers, same body → both responses normalised and compared.

**The diff ignores** (these legitimately differ and do not affect the tablet):
- `X-Correlation-Id` header (per-request id)
- `Date` header
- key order in JSON objects
- whitespace

**The diff fails on** anything else: a changed field name, a changed value, a number
formatted differently, a field that appears or disappears, a different status code.

Because the old host needs SQL Server and IIS, the parity run happens in **your
environment**, not here. The test project and its diff helper are provided ready to run;
what is verified here is: compiles, routes register, response shape matches the original
line by line.

## Rollback

Every step is independently reversible. The LegacyHost controller is not deleted when
its replacement ships — it is left in place, its route unmapped. If the new path
misbehaves, re-map the old route and the tablet is back on 4.8 within a deploy.

A LegacyHost controller is only deleted once its .NET 10 replacement has run a full
business day without a parity or error report.

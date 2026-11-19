# CMC_Logiwa design v1 (draft for review, 2026-11-13)

Nothing in this document is built yet. CMC_VER1 is not modified. Answers behind each decision are in `CMC_VER2-planning-questions.md`.

## 1. Shape of the system

```
Logiwa  --webhook-->  WEB VM (CMC_Logiwa_Integration, SQL, IIS)  <--HTTPS (outbound only)--  PACKING PC (CMC_Logiwa app + Local_CMC_Logiwa_DEV/PRO)  <--TCP-->  CMC machine
Logiwa  <--API--      WEB VM
```

- The packing PC only makes outbound HTTPS calls to the web VM. It never talks to Logiwa.
- The machine never waits on the network: ENQ is answered from the local DB only.
- 1 tracking number = 1 box = 1 label = 1 record (`PackJob`). Items are not stored.

## 2. Local database (`Local_CMC_Logiwa_DEV`, `Local_CMC_Logiwa_PRO`)

### PackJob (V1 columns and names kept so the handlers and procs barely change; typos fixed)

| Column | Type | Note |
|---|---|---|
| PackJobID | int identity PK | still the machine `Reference` |
| JobCreatedDateTime | datetime2 | when the row arrived/was created |
| JobNumber | nvarchar(50) | = tracking number (replaces the V1 textbox) |
| OrderNumber | nvarchar(50) | = tracking number, the scanned value |
| MatchLab | nvarchar(100) | = tracking number (CMC compares it to the printed label) |
| ShipmentIdentifier | nvarchar(64) | Logiwa unique order id |
| OrderCode | nvarchar(100) | client order number |
| ClientCode | nvarchar(50) | replaces TenantCode/TenantName |
| LabelData | nvarchar(max) | raw ZPL |
| LabelDataLen | int | |
| OrderStatus | nvarchar(20) | NoLabel, Ready, InProgress, Completed, Failed, Removed |
| ProcessStartedDateTime / ProcessCompletedDateTime | datetime2 | |
| PackID, BoxEventID, BoxEventText, FlagBoxLow, HasBoxIssue, BoxIssueComment, BoxIssueDateTime | as V1 | unchanged |
| BoxSizeH, BoxSizeL, BoxSizeW, BoxArea | int | mm, as received from CMC |
| WeightCarton, WeightScale | int | g, as received from CMC; WeightScale is the one pushed |
| MachineId | nvarchar(20) | from the machine message |
| ProcessedByUser | nvarchar(50) | logged-in operator |
| SyncSeq | bigint | web VM change counter when the row was last downloaded |
| IsCurrent | bit | exactly one current row per tracking number |
| ResetFromPackJobID, ResetByUser, ResetAt | | set on the new row created by a reset |
| PushStatus | nvarchar(10) | None, Pending, Sent |
| PushAttempts, PushNextAt, PushLastError, PushedAt | | outbox for the result push |

Indexes: unique filtered index on `OrderNumber WHERE IsCurrent = 1`; index on `(PushStatus, PushNextAt)`.

### Other tables
- `Users` (UserId, Username unique, PasswordHash, Salt, IsActive, CreatedAt): PBKDF2 salted hash. No roles.
- `PackJobEvent` (EventId, PackJobID, EventType, EventAt, UserName, Detail): ENQ, ACK, PRN, LAB, END, REM, Duplicate, Reject, Reset, LabelRequest. This is the audit trail.
- `LabelRequest` (RequestId, OrderNumber, MachineId, RequestedAt, Status, Attempts, NextAttemptAt, LastError): at most one open request per tracking number.
- `SyncState` (single row: LastSyncSeq, LastPollAt, LastPollError).

## 3. Scan (ENQ) logic, local only

Stored proc `sp_ENQ_Get_Data(@Barcode, @Counter, @MachineId, @User)` looks up the current row for the tracking number:

| Case | Reply to machine (`enq` result01 / description) | Side effect |
|---|---|---|
| no row (also not in PackJobArchive) | 0 / "Not found, try again later" | event; an immediate on-demand poll starts in the background (the reply does not wait for it), so a rescan a few seconds later finds the order if it has arrived |
| no current row but found in PackJobArchive | 0 / "Duplicate" | event; reset restores it from the archive as a new Ready row |
| row is Ready | 1 / tracking number, MatchLab, Reference = PackJobID | status -> InProgress, MachineId, user, started time |
| row is InProgress, Completed, Failed or Removed (processed from ENQ on) | 0 / "Duplicate" | event; WPF screen lists it so the operator can reset or reject |
| row is NoLabel | 0 / "No label, try again later" | one LabelRequest row (if none open) |

The rest of the flow (IND, ACK, PRN, LAB, END, REM) is the V1 handlers. END (successful) additionally stores weight/dimensions and sets `PushStatus = Pending`. A failed END or a REM stays local.

**Reset** (any operator, from the WPF screen): the current row gets `IsCurrent = 0`; a new row is created from it (same label, status Ready, `ResetFrom...` filled, new PackJobID). The operator rescans. **Reject** is local only: an event is written, nothing changes.

## 4. Web VM HTTPS contract (new endpoints in CMC_Logiwa_Integration)

Auth: per-PC API key in a header, HTTPS only, IP allow-list, key stored hashed on the web VM. All calls idempotent.

1. `GET /api/cmc/orders?afterSeq={n}&limit=100` returns `{ orders: [{ syncSeq, trackingNumber, shipmentIdentifier, orderCode, clientCode, labelZpl | null }], maxSeq, hasMore }`, gzip. Includes orders without a label (labelZpl null). When a label arrives later, `syncSeq` bumps and the row comes down again.
2. `POST /api/cmc/orders/ack` `{ maxSeq, machineId }`: web VM marks them delivered.
3. `POST /api/cmc/label-requests` `{ trackingNumber, machineId, requestedAt }`: web VM resets that order's label fetch so the worker retries.
4. `POST /api/cmc/results` `{ trackingNumber, packJobId, machineId, boxWeightG, lengthMm, widthMm, heightMm, completedAt }`: stored in a new `ShipmentResults` table (unique on trackingNumber + packJobId), answered 202. A web VM worker then calls Logiwa and retries on failure.

Web VM additions: a `SyncSeq` column fed by a SQL sequence on order/label rows, the `ShipmentResults` table, and the Logiwa return worker (below).

## 5. Return to Logiwa (web VM only)

Worker per result: resolve the license plate (List LPs where lpNumber = tracking number; cache identifier, warehouseIdentifier, licensePlateTypeIdentifier) -> GET the LP -> change only length, width, height, weight, actualVolume -> `PUT https://myapi.logiwa.com/v3.2/LicensePlate/update/{identifier}` with the full body (the PUT may replace unspecified fields, so the unchanged ones are sent back). Failure -> retry with backoff.

Units: local sends raw mm and g. The web VM converts with configuration (`WeightGramsDivisor`, default 1000 -> kg; dimensions mm; `actualVolume = L x W x H`). A unit change later is a config change, not a code change. Inch/lb data from the sandbox is normalised to mm/g on the way in.

## 6. Local app (CMC_Logiwa, .NET Framework 4.7.2, WPF)

- Cloned from CMC_VER1 into a sibling repo, own ClickOnce identity and key, no `origin` remote.
- Removed: 3PLWMS auth, wave import, `SessionVariables`, wave and shipment API services, the job number textbox.
- Kept: `NetworkManager`, `FormatProcessMessages`, `MessageRouter`, handlers, `SQLManager`, `Theme.xaml`.
- New: simple local login (username and password) against `Users`; `OrderSyncService` (timer, 20 s configurable, no overlapping runs, page by page, one JSON-per-page upsert proc, advances `LastSyncSeq`, then acks); `OutboxService` (result push and label requests, backoff 10 s up to 5 min, never gives up, shows pending count); a Duplicates/Reset screen; a status bar (last sync, pending pushes, errors).
- Daily archive job (no deleting): rows that are not current, or are Completed/Failed/Removed, have `PushStatus` of None or Sent and are older than N days (default 30, configurable) move in small batches to `PackJobArchive` (same columns plus `ArchivedAt`) inside one transaction (`DELETE ... OUTPUT INTO`). Rows with a pending push are never moved. `PackJobEvent` rows follow the same rule. The archive is kept until you decide otherwise.
- On-demand poll: a not-found scan triggers one immediate run of the sync (skipped if one is already running).
- Handlers keep swallowing DB/API exceptions so the machine always gets its reply.

## 7. Build order
1. Local DB script and procs (DEV), local login.
2. Web VM: `SyncSeq`, `GET orders`, `ack`, `label-requests`, `results` and the Logiwa return worker (with tests).
3. Clone and strip CMC_VER1, wire sync and outbox, new ENQ procs.
4. Test with the machine simulator or the real CMC on DEV, then the PRO script and subscription.

## 8. Open points
- DECIDED: weight goes to Logiwa in kg; CMC grams are converted (g / 1000) on the web VM before the Logiwa call, so nothing reaches Logiwa in grams. Dimensions in mm.
- DECIDED: not-found shows "Not found, try again later" and triggers an immediate poll.
- DECIDED: archive to `PackJobArchive` instead of deleting (default after 30 days, configurable).
- PRO: same contract, same app build; DEV and PRO differ only by configuration (web base URL, API key, database). Each environment issues its own per-PC API keys. Awaiting the user's OK.

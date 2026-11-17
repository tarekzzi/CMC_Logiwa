# CLAUDE.md

> **Work in progress.** CMC_Logiwa is the successor of CMC_VER1 (same machine protocol; no wave import, no 3PLWMS; orders come from the web VM instead). Sections below that mention the wave import, 3PLWMS login/API or the `CMC` database still describe V1 and are being rewritten as the new version is built. Done so far: local login (`LocalAuthService`), per-PC Database settings (`DbSettings`, DPAPI file under ProgramData), SQL scripts in `db/` (run order 001, 000, 002, 003, 004; 004 = procedures for the Settings / Overview / Errors screens). Design: `CMC_VER1DOCCMC_Logiwa-design-v1.md`. Database: `Local_CMC_Logiwa_DEV` / `_PRO`, SQL logins `CMC_Logiwa_app_dev` / `_pro`.

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

CMC_Logiwa is a WPF desktop app (.NET Framework 4.7.2, old-style `.csproj` + `packages.config`) that acts as the bridge between a **CMC CartonWrap packing machine** (TCP socket protocol) and the **3PLWMS WMS API** (`api1.3plwms.com.au`), using a local SQL Server database (`CMC`) as the working store. There are no tests.

## Build / run

Run from the solution directory (`CMC_Logiwa/`, the one containing `CMC_Logiwa.sln`):

```
nuget restore CMC_Logiwa.sln
msbuild CMC_Logiwa.sln /p:Configuration=Debug
CMC_Logiwa\bin\Debug\CMC_Logiwa.exe
```

- Needs Visual Studio / Build Tools MSBuild (not `dotnet build` — this is a non-SDK project). NuGet packages live in the solution-level `packages/` folder.
- New `.cs` files must be added as `<Compile Include=...>` entries in `CMC_Logiwa/CMC_Logiwa.csproj`; they are not picked up automatically.
- Deployment is ClickOnce (`publish/`, manifests signed with `the CMC_Logiwa signing certificate (CN=CMC_Logiwa, in the Windows user certificate store, thumbprint set in the csproj)`).
- The app needs network access to the SQL Server named in `App.config` (`CMCConnection`, currently `CMC_Control\CMC_Control`; alternative dev connection strings are commented out there) and to the WMS API.

## Architecture

**Startup flow:** `App.xaml` (`StartupUri`) → `LoginWindow` (authenticates via `AuthenticationService` against the 3PLWMS Users API) → stores ApiKey/Warehouse/UserEmail into the static `Models/SessionVariables` → opens `MainWindow`. All WMS API calls read `API-Key` / `User-Email` headers from `SessionVariables`.

**Main window (`MainWindow.xaml` + `.xaml.cs`)** has two independent responsibilities:
1. **Wave import:** user picks a wave (`GetListAllWavesByTypeAndStatuses`) → `WaveApiService.RequestCMCPackingTaskAsync` returns fulfilment numbers → for each, `ProcessCMCPackingTaskAsync` returns items (incl. label data) → saved to SQL via `sp_AddNewOrder`. Warehouse code is currently hard-coded to `"B"`.
2. **Machine connection:** creates a `NetworkManager` (IP, port, machine ID, job number textbox) and pipes its log events into the message log (`AppendLog`, marshalled with `Dispatcher.BeginInvoke`).

**Machine protocol pipeline** (the core of the app):
- `Services/NetworkManager.cs` — TCP client. Messages are ASCII framed as `<STX>payload<ETX>`; `SendAsync` takes the payload *without* framing. Also owns the CIS counter (`GetNextCisCounter`, 1..999999) used for messages the app originates.
- `Services/FormatProcessMessages.cs` — parses incoming pipe-delimited payloads (`MachineId|TYPE|Counter|Reference|...`) into `Models/MessageProcessingResult`, and builds all outgoing payloads (`Format*Response` / `FormatPrnRequest`).
- `Services/MessageRouter.cs` — switch on message type → `Handlers/*Handler`.
- **Case convention:** UPPERCASE type = request from the machine, lowercase = response. The app replies to each machine request with the lowercase echo (`ENQ`→`enq`, `LAB`→`lab`, etc.). The app's own `PRN` request gets a `prn` result back from the machine.

Message flow per carton:
| Machine sends | Handler | Stored proc | Reply / side effect |
|---|---|---|---|
| `HBT` | inline in router | — | `hbt` |
| `ENQ` (barcode scan) | `EnquiryHandler` | `sp_ENQ_Get_Data` | `enq` with PackJobID as reference + MatchLab |
| `IND` (inducted) | `InductedHandler` | — | `ind` |
| `ACK` | `AckHandler` | `sp_Update_ACK_Status`, `sp_Get_PRN_Data` | `ack`; if ACK result = 1, sends `PRN` with label data |
| `prn` (print result) | `PrnResultHandler` | `sp_Update_PRN` | — |
| `LAB` (label/measure) | `LabHandler` | `sp_Update_LAB` | `lab` with result + MatchLab |
| `END` (carton done) | `EndHandler` | `sp_Update_END` | calls WMS `UpdateShipmentInfo` (Completed/Failed + box dims/weights), then `end` |
| `REM` (removed) | `RemHandler` | `sp_Update_REM` | `rem` |

The `Reference` field carries the PackJobID assigned at ENQ time. Handlers swallow DB/API exceptions so the machine always gets its reply — preserve that when editing.

**Data access:** `Services/SLQManager.cs` (class `SQLManager`, file name is misspelled) — only exposes `ExecuteStoredProcedure(name, Dictionary<string, object>)` returning a `DataTable`. All business logic lives in SQL Server stored procedures, which are **not in this repo**.

**API endpoints:** `Helpers/ApiEndpoints.cs` (production URLs active; QA `qaapi.3plwms.com.au` URLs commented out above them). The auth endpoint is hard-coded separately in `AuthenticationService`.

## Conventions / gotchas

- Many files contain an older version of the whole file commented out at the top; the live code is below it.
- UI is WPF. All colours, fonts and control styles (primary/secondary/danger buttons, text boxes, combo box, scroll bars) live in `Theme.xaml`, merged in `App.xaml`; use its brushes/styles rather than hard-coding colours. New XAML files must be added to `CMC_Logiwa.csproj` as `<Page Include=...>` (code-behind as `<Compile>` with `DependentUpon`). Per-monitor DPI awareness is declared in `app.manifest`. The implicit `TextBlock` style is deliberately absent: setting Foreground there overrides button text colours — set `Foreground` on the window instead.
- Two JSON libraries are in use: `Newtonsoft.Json` (auth) and `System.Text.Json` (wave/shipment APIs).
- This folder is its own git repo, but it sits inside an unrelated repo rooted at `C:\Users\tarek\Desktop`; run git commands from here, never from a parent folder.

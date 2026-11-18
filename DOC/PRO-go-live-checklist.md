# CMC_Logiwa: PRO set-up and go-live checklist

DEV stays as it is (Logiwa **sandbox**) and is where every change is tried first. PRO uses the **same build and the same SQL scripts**;
only configuration differs. Tick the boxes in order. A step marked **(you decide)** needs a value only you can give.

| | DEV | PRO |
|---|---|---|
| Logiwa API | `https://myapisandbox.logiwa.com/v3.1/` | `https://myapi.logiwa.com/v3.1/` |
| Web VM `10.100.11.10` | site + pool `Logiwa_CMC_DEV`, `C:\inetpub\LogiwaCMC\DEV` | site + pool `Logiwa_CMC_PRO`, `C:\inetpub\LogiwaCMC\PRO` |
| Web DB (SQL VM `10.100.10.10`) | `Logiwa_CMC_DEV`, login `Logiwa_CMC_app_dev` | `Logiwa_CMC_PRO`, login `Logiwa_CMC_app_pro` |
| Key Vault | `kv-logiwa-cmc-dev` | `kv-logiwa-cmc-pro` |
| Packing PC DB (`CMC_CONTROL\CMC_CONTROL`) | `Local_CMC_Logiwa_DEV`, login `CMC_Logiwa_app_dev` | `Local_CMC_Logiwa_PRO`, login `CMC_Logiwa_app_pro` |
| API key packing PC to web VM | key A | **different** key B |

**Rule: nothing reaches PRO that has not run on DEV first.** Never share a key, a password or a database between the two.

---

## 0. Decisions before you start (you decide)
- [x] PRO hostname, `logiwacmc.fulfilmentaustralia.com.au` (decided; DNS record created, the wildcard certificate covers it).
- [x] Logiwa **production** API user exists (credentials are in the PRO vault).
- [x] Warehouse: **chosen in the app** (Settings > Machine drop-down, read from Logiwa): nothing to look up or configure on the web VM. Clients: **all clients** (decided), so no client GUID is configured.
- [x] Packing PC: **one PC, the same one that runs DEV today** (one API key; see section 6 for switching between DEV and PRO on it).
- [ ] Ask Logiwa the **exact status names** for shipped / delivered / cancelled / deleted orders (used by the shipped check). The user will provide them later; until then the default list applies.
- [ ] A go-live window when the machine is idle (no date yet).

## 1. Release the code (dev PC)
- [ ] CMC_Logiwa: merge `ui-redesign` into `main`, then tag: `git tag v1.0 && git push origin main v1.0` (add `-c http.sslBackend=schannel` if the certificate error appears).
- [ ] CMC_Logiwa_Integration: `git tag v1.0 && git push origin v1.0`.
- [ ] Web package: `deploy\Package.ps1` (writes `deploy\out\cmc-logiwa.zip`). Keep the zip with its tag: it is also the rollback.
- [ ] App package: Release build of `CMC_Logiwa`, zip it, note the SHA-256. Keep it with its tag.

## 2. SQL VM: web database `Logiwa_CMC_PRO`
- [ ] Create database `Logiwa_CMC_PRO` (same collation as DEV).
- [ ] Run `db\schema.sql` (Integration repo) on it with an admin login. It is the full EF migration script and safe to re-run.
- [ ] Create login `Logiwa_CMC_app_pro` with a new password, user in `Logiwa_CMC_PRO`, roles `db_datareader` + `db_datawriter` only (the app cannot change the schema).
- [ ] Put the connection string in the PRO vault (step 3). Write the password only in the vault and your password manager.

## 3. Key Vault `kv-logiwa-cmc-pro`
Create it like the DEV vault; the web VM's managed identity needs read access to secrets.
- [x] `ConnectionStrings--Sql` = connection string to `Logiwa_CMC_PRO` with `Logiwa_CMC_app_pro`
- [x] `Webhook--Secret` = a NEW long random URL-safe secret (not the DEV one)
- [x] `Logiwa--Email` and `Logiwa--Password` = the Logiwa production user
- [x] `CmcApi--ApiKeyHashes--0` = hash of the PRO key. Create it on the dev PC: `powershell -ExecutionPolicy Bypass -File CMC_VER1\DOC\New-CmcApiKey.ps1 -Environment PRO`.
      The KEY is shown once: store it in the password manager as "CMC API key PRO". Only the HASH goes to the vault. A second packing PC later = `CmcApi--ApiKeyHashes--1`.
- [x] Skipped for now (decided): `CmcApi--AllowedIps--0` = public IP of the packing PC (only if it is fixed; a wrong value locks the PC out).

## 4. Web VM `10.100.11.10`: PRO site
- [ ] Folder `C:\inetpub\LogiwaCMC\PRO` (with a `logs` folder), app pool `Logiwa_CMC_PRO` (No Managed Code, same idle/preload settings as DEV), site `Logiwa_CMC_PRO` bound to the PRO hostname on 443 with the certificate.
- [ ] App pool environment variables (plain settings only, no secrets):
  - `ASPNETCORE_ENVIRONMENT` = `Production`
  - `KeyVault__Uri` = the PRO vault URI
  - `Webhook__PublicBaseUrl` = `https://logiwacmc.fulfilmentaustralia.com.au`
  - `Logiwa__BaseUrl` = `https://myapi.logiwa.com/v3.1/` (DEV sets the sandbox URL; PRO must not)
  - `Webhook__AllowedIps__0` = `18.116.226.248`, `Webhook__AllowedIps__1` = `3.151.75.183` (Logiwa v1 webhook IPs; leave out only if a first test shows other addresses)
  - `OrderStatus__ClosedStatuses__0..n` = the exact Logiwa status names from step 0 (the default list is Shipped, Delivered, Cancelled, Canceled, Deleted)
- [ ] Deploy: copy `cmc-logiwa.zip` and `Install.ps1` to the VM, then (elevated PowerShell)
      `.\Install.ps1 -Url https://logiwacmc.fulfilmentaustralia.com.au/webhooks/logiwa -Target C:\inetpub\LogiwaCMC\PRO`
      The last step must report **405** for the GET (the route is POST only).
- [ ] Recycle the pool after any vault change: `%windir%\system32\inetsrv\appcmd recycle apppool /apppool.name:"Logiwa_CMC_PRO"`.
- [ ] Check the log in `C:\inetpub\LogiwaCMC\PRO\logs` for start-up errors (missing secret, SQL login).
- [ ] **Do not register the Logiwa webhook yet** (step 7). Until then no PRO order can arrive.

## 5. Packing PC: local database `Local_CMC_Logiwa_PRO`
On the packing PC, as a SQL sysadmin, from the `CMC_Logiwa\db` folder (the same scripts DEV uses; the installer swaps the names):
- [ ] `powershell -ExecutionPolicy Bypass -File .\Install-LocalDb.ps1 -Environment PRO`
      (add `-Auth Sql -AdminLogin <admin>` if Windows login is not sysadmin). It asks you to type **PRO**, then a password for `CMC_Logiwa_app_pro` (12+ characters, kept in memory only; store it in the password manager).
- [ ] It ends with a check: all procedures exist, `CMC_Logiwa_app_pro` can run them, event texts loaded. Run `-VerifyOnly` any time to repeat the check.
- [ ] Alternative for SSMS: `-ScriptsOnly` writes `db\generated\PRO\*.sql` (never committed); run them in the printed order.
- [ ] The SQL instance must allow SQL logins ("SQL Server and Windows Authentication"). If the app login cannot connect afterwards, check that first.
- [ ] No test data: PRO must not contain `TEST-SHIPMENT-n` rows or the test cartons.

## 6. Packing PC: the app
- [ ] Install/update the app from the tagged package (verify the SHA-256).
- [ ] First start: Database settings = server `CMC_CONTROL\CMC_CONTROL`, database `Local_CMC_Logiwa_PRO`, login `CMC_Logiwa_app_pro`, environment name **PRO** (the window then shows PRO next to your name and a red PRODUCTION banner across the top), web service address `https://logiwacmc.fulfilmentaustralia.com.au`, the **PRO** API key.
- [ ] Settings > Users: create the operators (PRO has its own user list).
- [ ] Settings > Machine: machine ID, IP, port, **the warehouse (drop-down read from Logiwa through the web service; required: the app asks at start-up, will not connect the machine and downloads no orders until it is chosen)**, automatic reconnect on. Saved in the PRO database.
- [ ] Message log shows "Web sync OK" and no 401/403.
- [ ] The same PC runs DEV and PRO, but the app has ONE database setting per PC: switching = Settings > Database and web service (not while the machine is connected). Users, machine ID/IP/port and the warehouse are stored per database, so enter them once in PRO too. Once PRO is live, leave this PC on PRO; use DEV only while the machine is idle or on another PC, never with DEV orders while real cartons are running.

## 7. Go-live
- [ ] Machine idle. Register the production webhook (web VM, in the site folder):
      `dotnet CmcLogiwa.Api.dll --register-webhook`  (prints the address with the secret masked; exit code 0 = ok; Logiwa may take a few minutes to activate it).
- [ ] Take **one real order** to ReadyToShip in Logiwa. Check in order: web VM log (webhook received, order fetched, label stored) -> packing PC sync receives it -> scan on the machine -> END -> web VM log "result" written -> weight and size on the Logiwa license plate.
- [ ] Check the shipped check: restart the app, the log shows "Logiwa check done ...".
- [ ] Check the dashboard counts it, and Errors shows nothing unexpected.
- [ ] Then release the rest of the orders.

## 8. Rollback
- Web VM: re-run `Install.ps1` with the previous tag's zip. The database only ever grows (migrations are additive), so no database rollback is needed.
- Packing PC: reinstall the previous app package. The local scripts only add objects (`CREATE OR ALTER`, `ADD` columns), so the old app keeps working with the newer database.
- Emergency stop of new orders: unregister or disable the webhook subscription in Logiwa; the packing PC keeps working through what it already has.

## 9. Continuing with DEV after go-live
1. Develop and test against DEV (sandbox) only.
2. Database change = a new numbered script in `CMC_Logiwa\db` (for example `008_..._dev.sql`, add it to `$Scripts` in `Install-LocalDb.ps1`); web database change = an EF migration and a regenerated `db\schema.sql`.
3. When DEV is proven: tag a new version, build the packages once, deploy the same files to PRO, run the same scripts with `-Environment PRO`.
4. Keep the last good packages next to their tags (rollback in 2 minutes).
5. Rotate the API keys and the Logiwa token credentials on a schedule; each rotation is a vault change plus a pool recycle.

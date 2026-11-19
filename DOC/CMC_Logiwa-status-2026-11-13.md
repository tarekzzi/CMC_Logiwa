# CMC_Logiwa status, 2026-11-13 (end of day)

## Where things are
- **App** `C:\Users\tarek\Desktop\Logiwa-CMC\CMC_Logiwa` (own git repo, branch `ui-redesign`, no remote). Renamed from CMC_VER1, no wave import, no 3PLWMS, local username/password login, per-PC Database settings screen (DPAPI file under ProgramData), message log with failures in red. CMC_VER1 itself is untouched.
- **Database** `Local_CMC_Logiwa_DEV`:
  - dev PC (`TAREK-ZAKI\TAREKSMART2025`): up to date (checked read-only), holds only the 3 test cartons, never used on the machine.
  - CMC PC (`CMC_CONTROL\CMC_CONTROL`): created by `run-setup.ps1`, **update 1 deployed** (removed cartons store event 010 "Box Removed", front panel names the tracking number).
- **Packages for the CMC PC** `DOC\packing-pc\` (setup, app zip, results export, update-1). Scripts there need the in-memory form on the CMC PC because script execution is disabled.

## What is proven on the real machine (machine id 002, 10.2.1.201:9200)
Full flow ENQ -> ACK -> PRN ok -> LAB ok -> END ok with event 000 GOOD PRODUCT; "no label" reply; duplicate reply (also while in progress); reset creating a new record and keeping the old one; REM (removed carton, also a forced reject at the machine) recorded as Removed / 010 Box Removed; every completed carton queued for sending (PushStatus Pending). Sizes 202-310 x 250-310 x 55-80 mm and weights 135-295 g (scale weight; units mm and g, not yet checked against a real carton).

## Open issue to repeat next week: the barcode that stayed "Duplicate"
User's account: second round of duplicates + resets for 1JDQ12002727 / 1JDQ12002728; trying 728 several times before 727 showed "Duplicate" on the CMC panel (not on the app's Duplicate list); after 727 was processed, 728 was accepted with no further reset. One barcode visible at a time (page folded). The machine does not decide duplicates itself.
Evidence (message log + PackJobEvent, 16:23:00-16:25:23): no ENQ for 728 reached the app after the reset; the machine's counters run 63, 64, 65 with no gap; the only Duplicate the app sent in that window was for 727 (16:25:19, in progress). So the "Duplicate" the panel showed for 728 was the app's last reply (16:23:00, before the reset) staying on screen: the new scans of 728 never reached the app.
Pattern in all rounds: after the app rejects a barcode (reply result 0), the first scan after the resets was always a DIFFERENT barcode, except this one failing case where 728 (the last rejected barcode) was tried first. A repeat of the same barcode after an ACCEPTED scan does reach the app (727 at 15:12:51 and again 15:13:51; 728 at 15:14:55 and 15:16:04; 727 at 16:24:28 and 16:25:19). Working theory (unproven): the machine/scanner does not send an ENQ again for the barcode that was just rejected until a different barcode has been read.

### Test plan (next week, with the message log open; photo of the CMC panel at each step)
1. Scan 727 and 728 so both reply Duplicate (note which is scanned last, call it X).
2. Reset both in the app.
3. Scan X three times. Note after each scan: does an `RECV ...|ENQ|...|X|...` line appear? Does the panel change?
4. Scan the other barcode (accepted), then X again. Expect accepted.
5. Repeat steps 1-4 once with the other barcode last, and once rejecting with the "No label" barcode TESTNOLABEL1 as the last rejected scan.
6. Press Export... in the log and run `export-test-results.ps1` on the CMC PC straight after, and bring both files.
If ENQ lines DO appear for X in step 3 and the app answers Duplicate while the record is Ready, that is an app fault and needs the record state at that moment.

## Other loose ends (machine phase)
- Reject reason: a REM message has no reason field, only the reference; no END/failed message was seen for a rejected carton. Question for the CMC supplier if a reject reason must be told apart from a removal.
- Confirm units against one real carton (weight in g, sizes in mm).
- Not yet tested on purpose: label mismatch, LAB/END failure with a reason shown in red.
- The app's message log only lives in memory (export it before closing). Daily log file under ProgramData was proposed, no answer yet.
- `export-test-results.ps1`: use the new copy (prints every column; old copy cut off columns and showed 1-row tables as empty).

## Design points already known for the next phase (not started; the user asked not to be offered the VM work until the machine part is finished)
- Local app talks only to the web VM over HTTPS (poll orders, acknowledge, label requests, result push); web VM talks to Logiwa. Return call is `PUT https://myapi.logiwa.com/v3.2/LicensePlate/update/{identifier}`, weight in kg (g / 1000), dimensions in mm.
- After a reset, older Pending results for the same tracking number must be cancelled so a late retry cannot overwrite the newer measurement (currently the old Completed record stays Pending next to the new one).

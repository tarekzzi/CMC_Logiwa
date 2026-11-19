# CMC_VER2 planning: questions (2026-11-13)

Goal: a copy of CMC_VER1 (machine operation identical, no wave processing) that gets its orders from the Logiwa integration database on the SQL VM instead of the wave import.

Decision so far: duplicate CMC_VER1 into a sibling folder `CMC_VER2` (git clone, remove origin, own ClickOnce identity and signing key, dev/copy DB connection string). CMC_VER1 is not modified.

## Questions (answers below each)

1. Direction and trigger: does local pull from the SQL VM, or does the VM push to local? What is reachable from where (VPN, linked server, firewall)?
   Answer:

2. Transport: linked server / direct SQL access, or an API exposed by the integration that CMC_VER2 polls, or something else?
   Answer:

3. Handoff point: at which status does an order become available locally? Assumed LabelFetched -> PendingDelivery -> Delivered.
   Answer:

4. Mapping: what local data does the machine need per carton, and from where? (tracking/LP as the ENQ barcode, ZPL for the PRN, box and order details.) Keep sp_AddNewOrder as the entry point or write to its tables directly? Need its definition / the table schema.
   Answer:

5. Return path: after END, who sends the result (dimensions, weight, completed/failed) to Logiwa? Local app writes to the VM DB and the integration calls Update License Plate, or local app calls Logiwa itself (as UpdateShipmentInfo does now)?
   Answer:

6. Failure handling: duplicates, order cancelled/changed after delivery, local SQL offline; retries and acknowledgement?
   Answer:

7. Scope: does the wave import stay alongside, or is it replaced entirely?
   Answer:

## Decisions added 2026-11-13 (from the user)

- New project name and identity: CMC_Logiwa (folder, assembly, ClickOnce). VER1 untouched.
- New database (new fields to match Logiwa calls and respond back).
- New simple local login (username and password, no 2FA, no 3PLWMS APIs).
- Scan value = tracking number from Logiwa. Not found -> "order not exist". Already processed -> duplicate, user may reset or reject. No label -> "no label, try again later" and push a label request to the WEB VM. Match -> continue as V1.
- On END, weight and dimensions go to the WEB VM; WEB VM updates Logiwa. No direct local <-> Logiwa communication. Logiwa does not wait for completion status.
- Reference: current local table `dbo.PackJob` (PackJobID, JobNumber, OrderNumber nvarchar(20), Box sizes/weights, MatchLab, LabelData, item fields).

## Evaluation of PackJob (proposed)

Split into Carton (one row per tracking number) + CartonItem + CartonEvent history + sync outbox. Widen OrderNumber and unique-index it. Add Logiwa order id, ShipmentOrders id, LP, tracking, carrier, warehouse, sync state/attempts/errors, operator, machine id, rowversion. Fixed status set. Keep raw + converted units. Fix typos (BoxSzieL).

## More questions (answers below each)

8. Local -> web VM link: direct SQL or HTTPS API (preferred, outbound only)?
   Answer:
9. Pre-sync orders locally or live lookup at scan time? (pre-sync must include orders with no label yet)
   Answer:
10. Link down when a result is ready: local outbox with retries OK? How long can results wait?
   Answer:
11. Duplicate decision cannot hold the machine reply: reply reject immediately, operator resets in the WPF screen and rescans?
   Answer:
12. What counts as "already processed": END completed only, or ENQ done / REM too?
   Answer:
13. Who may reset (any operator or supervisor with a reason)? New PackJob row per attempt, history kept?
   Answer:
14. What does reject do: local log only, or also flag to web VM/Logiwa?
   Answer:
15. What should Logiwa receive: only weight/dimensions per tracking number, or also completed/failed? What about END failed or removed cartons?
   Answer:
16. Multi-package orders: each tracking number a separate carton scan and separate Logiwa update?
   Answer:
17. Login: local Users table, hashed passwords, roles (Operator/Admin, Supervisor?), who creates/resets users?
   Answer:
18. Names: database name (CMC_Logiwa?), SQL instance for dev and production?
   Answer:
19. Label request to web VM: anything beyond tracking number (machine id, time)?
   Answer:

## User answers 2026-11-13 (round 2)

- Q1/2: 1 tracking = 1 box = 1 label = 1 record; item lines are not needed. Widen the tracking field in the new DB.
- Q3: add the missing Logiwa/sync fields to the new table (they do not affect the machine process).
- Q4: agreed (fixed status set, etc). Q6: fix typos/leftovers without changing the process.
- Q5: CMC sends mm and g; Logiwa expects mm and kg: convert (g/1000). Logiwa sandbox may give inches/lb: always normalise to mm / g / kg.
- Q8: HTTPS calls (local outbound only).
- Q9: regular polling of orders from the web VM, ahead of ENQ, without overloading memory (design proposed in chat).
- Q10: weight/dimension push must retry. Q11: agreed (reject immediately, reset in WPF, rescan). Q12: "processed" starts from ENQ.
- Q13: any operator may reset; new record per attempt. Q14: reject is local only.
- Q15: only weight and dimensions on successful END; follow the Logiwa API the user shared (not in the repo: ask for it again).
- Q16: each tracking number = 1 order = 1 record (carrier creates one tracking per box).
- Q17: simple username and password, no roles. Q18: databases Local_CMC_Logiwa_DEV and Local_CMC_Logiwa_PRO.
- Q19: need the label as raw ZPL; wants a separate field for the actual job number received from Logiwa. OrderNumber = tracking number; MatchLab = tracking number and label (to be clarified).

## Open follow-ups (round 3), answers below each

20. Logiwa job number: which Logiwa field is it (wave number, shipment order number, other)? V1's sp_Update_END returns FulfilmentNumber and WaveNumber, and V1 also has a job number textbox that goes to the machine. Keep that textbox in the new app?
   Answer:
21. MatchLab: please run `SELECT TOP 5 PackJobID, OrderNumber, JobNumber, MatchLab, LEN(LabelData) FROM dbo.PackJob ORDER BY PackJobID DESC` in the V1 CMC database and paste it into DOC, so I can see exactly what MatchLab holds today.
   Answer:
22. V1 sends two weights (EndWeightCarton, EndWeightScale) plus H/L/W and area. Which weight goes to Logiwa as the box weight? And is area needed by Logiwa?
   Answer:
23. Logiwa API doc for the update call (weight/dimensions per license plate / tracking): please re-share (put in DOC).
   Answer:
24. Machine ID: needed locally? (explained in chat)
   Answer:

## User answers 2026-11-13 (round 3)

- Q22: only Box_Weight from EndWeightScale goes to Logiwa (Logiwa accepts one weight). Area is kept locally, Logiwa does not need it.
- Q20: no wave number any more. Local JobNumber is replaced by the tracking number (CMC identifies orders by it). New fields: ShipmentIdentifier (unique order number from Logiwa) and OrderCode (client order number). MatchLab = tracking number (CMC matches the printed carrier label against the processed order, to avoid mixing).
- Q24: one machine ID per PC; Logiwa does not care about machine ID but we record it locally (multiple CMC machines possible).
- Q23: Logiwa call = `https://myapi.logiwa.com/v3.2/LicensePlate/update/{identifier}` with body: identifier, warehouseIdentifier, licensePlateTypeIdentifier, lpNumber, alternateLPNBarcode, length, width, height, weight (4.5 = kg), actualVolume (12x10x8 = 960).

V1 facts found while reading code: the job number is a UI textbox, sent to sp_ENQ_Get_Data as @JobNumber and echoed as the description in the enq reply (shown on the CMC panel); errors ("Not Found", "Duplicate Record") also travel in that description field, result01 = 0.

## Open follow-ups (round 4), answers below each

25. Remove the job number textbox in the new app; the enq description (panel text) = tracking number on success and the error text on failure ("Order not exist", "Duplicate", "No label, try again later")?
   Answer:
26. Update License Plate: HTTP method (PUT/POST)? Does it replace the whole LP (so the web VM should GET it first and send back unchanged fields)?
   Answer:
27. Units of length/width/height and actualVolume in Logiwa (mm and mm3? or per-warehouse setting)? Test in sandbox.
   Answer:
28. Only successful END pushes to Logiwa; failed END (FinalResult 0) and REM stay local?
   Answer:

Not an issue: UPS sandbox label problem (UPS was for testing only, not used in production).

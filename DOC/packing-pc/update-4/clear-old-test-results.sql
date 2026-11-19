/* Packing PC database (CMC). Run BEFORE installing update 4.
   The 11 finished test cartons of 1JDQ12002727 and 1JDQ12002728 are still waiting to be sent (PushStatus Pending).
   Marking them 'None' means: never send. Nothing is deleted; the measurements stay in the table. */

-- 1) what will change (expect 11 rows, all OrderStatus Completed)
SELECT PackJobID, OrderNumber, OrderStatus, PushStatus FROM dbo.PackJob
WHERE PushStatus = N'Pending' AND OrderNumber IN (N'1JDQ12002727', N'1JDQ12002728');

-- 2) change them
UPDATE dbo.PackJob SET PushStatus = N'None'
WHERE PushStatus = N'Pending' AND OrderNumber IN (N'1JDQ12002727', N'1JDQ12002728');

-- 3) nothing else may be Pending now (expect 0 rows)
SELECT PackJobID, OrderNumber, OrderStatus, PushStatus FROM dbo.PackJob WHERE PushStatus = N'Pending';

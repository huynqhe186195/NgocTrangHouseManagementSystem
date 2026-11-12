-- D04 disposable TWO-DATABASE evidence/approval and crash-window rehearsal.
-- psql meta-command \connect switches independent PostgreSQL DATABASES,
-- not just two schemas in one transaction. NEVER run on real data.
-- ONLY call from the branch-controlled Windows local test script after its
-- disposable postgres:16 container and isolated EF payOS tables exist.
\set ON_ERROR_STOP on

\connect payos_ef_migration_test

CREATE SCHEMA d04_acceptance;

-- Real payOS staging tables are seeded with a synthetic confirmed intent and
-- its signed/reconciled webhook inbox; NO live provider/API call is performed.
INSERT INTO public."BuildingManagementPayOsPaymentIntents"
("Id","BuildingId","PayerId","PaymentId","OrderCode","AmountVnd",
 "PaymentLinkId","ProviderReference","ExpiresAtUtc","ConfirmedAtUtc",
 "Status","ExtraProperties","ConcurrencyStamp")
VALUES
('00000000-0000-0000-0000-00000000d401',
 '00000000-0000-0000-0000-00000000d402',
 '00000000-0000-0000-0000-00000000d403',
 '00000000-0000-0000-0000-00000000d404',
 91665001,150000,'D04-SYNTHETIC-LINK','D04-SYNTHETIC-REF',
 now()+interval '1 day',now(),2,'{}','d04-i-1');

INSERT INTO public."BuildingManagementPayOsWebhookInbox"
("Id","BuildingId","PaymentIntentId","OrderCode","ProviderReference",
 "PayloadSha256","ReceivedAtUtc","ReviewedAtUtc","Status","ExtraProperties","ConcurrencyStamp")
VALUES
('00000000-0000-0000-0000-00000000d405',
 '00000000-0000-0000-0000-00000000d402',
 '00000000-0000-0000-0000-00000000d401',
 91665001,'D04-SYNTHETIC-REF',repeat('a',64),now(),now(),1,'{}','d04-inbox-1');

-- Concrete candidate D04 approval schema; staging TEST ONLY, not an EF
-- migration or production authority. Actual bank verification/Identity
-- permissions are not provided by payOS and are STILL MISSING in runtime.
CREATE TABLE d04_acceptance.independent_reviews(
  id uuid PRIMARY KEY,
  payment_intent_id uuid NOT NULL UNIQUE,
  building_id uuid NOT NULL,
  payment_id uuid NOT NULL UNIQUE,
  order_code bigint NOT NULL CHECK (order_code > 0),
  amount_vnd bigint NOT NULL CHECK (amount_vnd > 0),
  bank_reference varchar(128) NOT NULL UNIQUE,
  document_sha256 varchar(64) NOT NULL CHECK (document_sha256 ~ '^[0-9a-f]{64}$'),
  reviewer_id uuid NOT NULL,
  evidence_verified_at_utc timestamptz NOT NULL,
  UNIQUE(payment_intent_id,reviewer_id,document_sha256),
  FOREIGN KEY(payment_intent_id,building_id,order_code)
    REFERENCES public."BuildingManagementPayOsPaymentIntents"("Id","BuildingId","OrderCode")
    ON DELETE RESTRICT
);
CREATE TABLE d04_acceptance.owner_approvals(
  id uuid PRIMARY KEY,
  payment_intent_id uuid NOT NULL UNIQUE,
  reviewer_id uuid NOT NULL,
  owner_id uuid NOT NULL CHECK (owner_id <> reviewer_id),
  reviewed_evidence_sha256 varchar(64) NOT NULL
    CHECK (reviewed_evidence_sha256 ~ '^[0-9a-f]{64}$'),
  approved_at_utc timestamptz NOT NULL,
  FOREIGN KEY(payment_intent_id,reviewer_id,reviewed_evidence_sha256)
    REFERENCES d04_acceptance.independent_reviews(payment_intent_id,reviewer_id,document_sha256)
    ON DELETE RESTRICT
);
CREATE FUNCTION d04_acceptance.reject_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'D04 evidence/approval records are append-only'
    USING ERRCODE='23514';
END;
$$;
CREATE TRIGGER review_no_mutation BEFORE UPDATE OR DELETE
  ON d04_acceptance.independent_reviews
  FOR EACH ROW EXECUTE FUNCTION d04_acceptance.reject_mutation();
CREATE TRIGGER approval_no_mutation BEFORE UPDATE OR DELETE
  ON d04_acceptance.owner_approvals
  FOR EACH ROW EXECUTE FUNCTION d04_acceptance.reject_mutation();

INSERT INTO d04_acceptance.independent_reviews
 (id,payment_intent_id,building_id,payment_id,order_code,amount_vnd,
  bank_reference,document_sha256,reviewer_id,evidence_verified_at_utc)
SELECT
 '00000000-0000-0000-0000-00000000d406', i."Id",i."BuildingId",
 i."PaymentId",i."OrderCode",i."AmountVnd",i."ProviderReference",
 repeat('b',64),'00000000-0000-0000-0000-00000000d407',now()
FROM public."BuildingManagementPayOsPaymentIntents" i
JOIN public."BuildingManagementPayOsWebhookInbox" w
  ON w."PaymentIntentId"=i."Id" AND w."BuildingId"=i."BuildingId"
  AND w."OrderCode"=i."OrderCode" AND w."ProviderReference"=i."ProviderReference"
WHERE i."Id"='00000000-0000-0000-0000-00000000d401'
 AND i."Status"=2 AND w."Status"=1;

INSERT INTO d04_acceptance.owner_approvals
 (id,payment_intent_id,reviewer_id,owner_id,reviewed_evidence_sha256,approved_at_utc)
VALUES
 ('00000000-0000-0000-0000-00000000d408',
  '00000000-0000-0000-0000-00000000d401',
  '00000000-0000-0000-0000-00000000d407',
  '00000000-0000-0000-0000-00000000d409',repeat('b',64),now());

DO $$
DECLARE refused boolean;
BEGIN
  -- A same-person Owner approval must fail even if a caller tries to insert it.
  refused:=false;
  BEGIN
    INSERT INTO d04_acceptance.owner_approvals
    (id,payment_intent_id,reviewer_id,owner_id,reviewed_evidence_sha256,approved_at_utc)
    VALUES ('00000000-0000-0000-0000-00000000d410',
      '00000000-0000-0000-0000-00000000d401',
      '00000000-0000-0000-0000-00000000d407',
      '00000000-0000-0000-0000-00000000d407',repeat('b',64),now());
  EXCEPTION WHEN check_violation THEN refused:=true; END;
  IF NOT refused THEN RAISE EXCEPTION 'Same-actor approval unexpectedly accepted'; END IF;

  -- Do not permit an approval or evidence to be changed after review.
  refused:=false;
  BEGIN
    UPDATE d04_acceptance.independent_reviews SET amount_vnd=1
      WHERE payment_intent_id='00000000-0000-0000-0000-00000000d401';
  EXCEPTION WHEN check_violation THEN refused:=true; END;
  IF NOT refused THEN RAISE EXCEPTION 'Review mutation unexpectedly accepted'; END IF;

  refused:=false;
  BEGIN
    DELETE FROM d04_acceptance.owner_approvals
      WHERE payment_intent_id='00000000-0000-0000-0000-00000000d401';
  EXCEPTION WHEN check_violation THEN refused:=true; END;
  IF NOT refused THEN RAISE EXCEPTION 'Approval deletion unexpectedly accepted'; END IF;

  IF (SELECT count(*) FROM d04_acceptance.independent_reviews) <> 1 OR
     (SELECT count(*) FROM d04_acceptance.owner_approvals) <> 1 THEN
     RAISE EXCEPTION 'Expected one immutable review and one distinct-actor approval';
  END IF;
  RAISE NOTICE 'D04 STAGING DB: independent evidence/approval append-only gates PASS';
END $$;

-- Switch to a second DATABASE. No transaction may span these \connects.
\connect payos_d04_legacy_test

CREATE SCHEMA d04_acceptance;
CREATE TABLE d04_acceptance.payments(
 id uuid PRIMARY KEY,
 building_id uuid NOT NULL,
 payer_id uuid NOT NULL,
 contract_id uuid NOT NULL,
 amount_vnd bigint NOT NULL CHECK(amount_vnd > 0),
 status smallint NOT NULL DEFAULT 0 CHECK(status IN (0,1)),
 bank_reference varchar(128),
 completed_at_utc timestamptz,
 CHECK ((status=0 AND completed_at_utc IS NULL) OR
        (status=1 AND bank_reference IS NOT NULL AND completed_at_utc IS NOT NULL))
);
CREATE UNIQUE INDEX ux_d04_payment_bank_ref
 ON d04_acceptance.payments(bank_reference) WHERE bank_reference IS NOT NULL;

CREATE TABLE d04_acceptance.receipts(
 id uuid PRIMARY KEY,
 payment_id uuid NOT NULL UNIQUE REFERENCES d04_acceptance.payments(id),
 payos_intent_id uuid NOT NULL UNIQUE,
 source_key varchar(80) NOT NULL UNIQUE,
 bank_reference varchar(128) NOT NULL UNIQUE,
 amount_vnd bigint NOT NULL CHECK(amount_vnd > 0),
 approved_by uuid NOT NULL,
 evidence_sha256 varchar(64) NOT NULL CHECK(evidence_sha256 ~ '^[0-9a-f]{64}$'),
 created_at_utc timestamptz NOT NULL
);
CREATE TABLE d04_acceptance.deposit_settlements(
 id uuid PRIMARY KEY,
 payment_id uuid NOT NULL UNIQUE REFERENCES d04_acceptance.payments(id),
 receipt_id uuid NOT NULL UNIQUE REFERENCES d04_acceptance.receipts(id),
 amount_vnd bigint NOT NULL CHECK(amount_vnd > 0)
);
CREATE TABLE d04_acceptance.outbox(
 id uuid PRIMARY KEY,
 receipt_id uuid NOT NULL UNIQUE REFERENCES d04_acceptance.receipts(id),
 source_key varchar(80) NOT NULL UNIQUE,
 event_type varchar(80) NOT NULL CHECK(event_type='D04PayOsReceiptCommitted'),
 created_at_utc timestamptz NOT NULL,
 acknowledged_at_utc timestamptz
);

INSERT INTO d04_acceptance.payments(id,building_id,payer_id,contract_id,amount_vnd)
VALUES
('00000000-0000-0000-0000-00000000d404',
 '00000000-0000-0000-0000-00000000d402',
 '00000000-0000-0000-0000-00000000d403',
 '00000000-0000-0000-0000-00000000d411',150000),
('00000000-0000-0000-0000-00000000d412',
 '00000000-0000-0000-0000-00000000d413',
 '00000000-0000-0000-0000-00000000d414',
 '00000000-0000-0000-0000-00000000d415',150000);

-- Simulated PROCESS CRASH before legacy DB COMMIT: all rows must roll back.
BEGIN ISOLATION LEVEL SERIALIZABLE;
SELECT id FROM d04_acceptance.payments
 WHERE id='00000000-0000-0000-0000-00000000d404' FOR UPDATE;
UPDATE d04_acceptance.payments SET status=1,bank_reference='D04-SYNTHETIC-REF',
 completed_at_utc=now() WHERE id='00000000-0000-0000-0000-00000000d404';
INSERT INTO d04_acceptance.receipts
(id,payment_id,payos_intent_id,source_key,bank_reference,amount_vnd,approved_by,
 evidence_sha256,created_at_utc)
VALUES('00000000-0000-0000-0000-00000000d416',
'00000000-0000-0000-0000-00000000d404',
'00000000-0000-0000-0000-00000000d401',
'PAYOS:D04:0000000000000000000000000000d401','D04-SYNTHETIC-REF',
150000,'00000000-0000-0000-0000-00000000d409',repeat('b',64),now());
INSERT INTO d04_acceptance.deposit_settlements
 (id,payment_id,receipt_id,amount_vnd)
VALUES('00000000-0000-0000-0000-00000000d417',
'00000000-0000-0000-0000-00000000d404',
'00000000-0000-0000-0000-00000000d416',150000);
INSERT INTO d04_acceptance.outbox
 (id,receipt_id,source_key,event_type,created_at_utc)
VALUES('00000000-0000-0000-0000-00000000d418',
'00000000-0000-0000-0000-00000000d416',
'PAYOS:D04:0000000000000000000000000000d401',
'D04PayOsReceiptCommitted',now());
ROLLBACK;
DO $$
BEGIN
 IF (SELECT status FROM d04_acceptance.payments
     WHERE id='00000000-0000-0000-0000-00000000d404') <> 0 OR
    (SELECT count(*) FROM d04_acceptance.receipts) <> 0 OR
    (SELECT count(*) FROM d04_acceptance.deposit_settlements) <> 0 OR
    (SELECT count(*) FROM d04_acceptance.outbox) <> 0 THEN
    RAISE EXCEPTION 'CRASH BEFORE COMMIT produced partial financial state';
 END IF;
 RAISE NOTICE 'D04 LEGACY DB: rollback before commit left zero financial rows PASS';
END $$;

-- Simulate successful commit followed by PROCESS CRASH before staging ack.
BEGIN ISOLATION LEVEL SERIALIZABLE;
SELECT id FROM d04_acceptance.payments
 WHERE id='00000000-0000-0000-0000-00000000d404' FOR UPDATE;
UPDATE d04_acceptance.payments SET status=1,bank_reference='D04-SYNTHETIC-REF',
 completed_at_utc=now() WHERE id='00000000-0000-0000-0000-00000000d404';
INSERT INTO d04_acceptance.receipts
(id,payment_id,payos_intent_id,source_key,bank_reference,amount_vnd,approved_by,
 evidence_sha256,created_at_utc)
VALUES('00000000-0000-0000-0000-00000000d416',
'00000000-0000-0000-0000-00000000d404',
'00000000-0000-0000-0000-00000000d401',
'PAYOS:D04:0000000000000000000000000000d401','D04-SYNTHETIC-REF',
150000,'00000000-0000-0000-0000-00000000d409',repeat('b',64),now());
INSERT INTO d04_acceptance.deposit_settlements
 (id,payment_id,receipt_id,amount_vnd)
VALUES('00000000-0000-0000-0000-00000000d417',
'00000000-0000-0000-0000-00000000d404',
'00000000-0000-0000-0000-00000000d416',150000);
INSERT INTO d04_acceptance.outbox
 (id,receipt_id,source_key,event_type,created_at_utc)
VALUES('00000000-0000-0000-0000-00000000d418',
'00000000-0000-0000-0000-00000000d416',
'PAYOS:D04:0000000000000000000000000000d401',
'D04PayOsReceiptCommitted',now());
COMMIT;

DO $$
DECLARE refused boolean;
BEGIN
  -- Cross-building use of the same bank reference must never create 2 receipts.
  refused:=false;
  BEGIN
    INSERT INTO d04_acceptance.receipts
     (id,payment_id,payos_intent_id,source_key,bank_reference,amount_vnd,
      approved_by,evidence_sha256,created_at_utc)
    VALUES('00000000-0000-0000-0000-00000000d419',
     '00000000-0000-0000-0000-00000000d412',
     '00000000-0000-0000-0000-00000000d420',
     'PAYOS:D04:0000000000000000000000000000d420',
     'D04-SYNTHETIC-REF',150000,
     '00000000-0000-0000-0000-00000000d421',repeat('c',64),now());
  EXCEPTION WHEN unique_violation THEN refused:=true; END;
  IF NOT refused THEN RAISE EXCEPTION 'Cross-building duplicate bank reference accepted'; END IF;

  -- A repeated coordinator may read the same source, never credit again.
  INSERT INTO d04_acceptance.receipts
  (id,payment_id,payos_intent_id,source_key,bank_reference,amount_vnd,
   approved_by,evidence_sha256,created_at_utc)
  VALUES('00000000-0000-0000-0000-00000000d422',
     '00000000-0000-0000-0000-00000000d404',
     '00000000-0000-0000-0000-00000000d401',
     'PAYOS:D04:0000000000000000000000000000d401',
     'D04-SYNTHETIC-REF',150000,
     '00000000-0000-0000-0000-00000000d409',repeat('b',64),now())
  ON CONFLICT DO NOTHING;

  IF (SELECT count(*) FROM d04_acceptance.receipts) <> 1 OR
     (SELECT count(*) FROM d04_acceptance.deposit_settlements) <> 1 OR
     (SELECT count(*) FROM d04_acceptance.outbox) <> 1 OR
     (SELECT status FROM d04_acceptance.payments
       WHERE id='00000000-0000-0000-0000-00000000d404') <> 1 THEN
     RAISE EXCEPTION 'Legacy committed receipt/journal/outbox not exactly-once';
  END IF;
  RAISE NOTICE 'D04 LEGACY DB: committed receipt + settlement + outbox and replay PASS';
END $$;

-- Staging payOS DB must still have NOT Posted after the legacy commit.
-- The PowerShell coordinator independently verifies the exact legacy
-- receipt/settlement/outbox and staging approval before issuing the ack.
\connect payos_ef_migration_test
DO $$
BEGIN
 IF (SELECT "Status" FROM public."BuildingManagementPayOsPaymentIntents"
     WHERE "Id"='00000000-0000-0000-0000-00000000d401') <> 2 THEN
   RAISE EXCEPTION 'Staging auto-marked posted without verified legacy proof';
 END IF;
 RAISE NOTICE 'D04 CROSS-DB: legacy committed while payOS remains pending ACK PASS';
END $$;

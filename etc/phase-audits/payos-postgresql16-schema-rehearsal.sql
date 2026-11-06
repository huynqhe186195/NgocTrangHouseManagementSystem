-- Disposable PostgreSQL 16 acceptance rehearsal for payOS schema invariants.
-- NOT an EF migration and MUST NEVER run against customer or production DBs.
\set ON_ERROR_STOP on
BEGIN;
CREATE SCHEMA IF NOT EXISTS payos_rehearsal;
CREATE TABLE payos_rehearsal.payment_intents (
  id uuid PRIMARY KEY,
  building_id uuid NOT NULL,
  payer_id uuid NOT NULL,
  order_code bigint NOT NULL UNIQUE CHECK (order_code > 0),
  amount_vnd bigint NOT NULL CHECK (amount_vnd > 0),
  payment_link_id varchar(128) UNIQUE,
  provider_reference varchar(128) UNIQUE,
  invoice_id uuid,
  payment_id uuid UNIQUE,
  expires_at_utc timestamptz NOT NULL,
  confirmed_at_utc timestamptz,
  status integer NOT NULL CHECK (status BETWEEN 0 AND 4),
  CONSTRAINT ck_payment_target CHECK ((invoice_id IS NULL) <> (payment_id IS NULL))
);
CREATE TABLE payos_rehearsal.webhook_inbox (
  id uuid PRIMARY KEY,
  building_id uuid NOT NULL,
  payment_intent_id uuid NOT NULL REFERENCES payos_rehearsal.payment_intents(id) ON DELETE RESTRICT,
  order_code bigint NOT NULL CHECK (order_code > 0),
  provider_reference varchar(128) NOT NULL UNIQUE,
  payload_sha256 varchar(64) NOT NULL CHECK (length(payload_sha256) = 64),
  received_at_utc timestamptz NOT NULL,
  reviewed_at_utc timestamptz,
  status integer NOT NULL CHECK (status BETWEEN 0 AND 2)
);
CREATE UNIQUE INDEX ux_inbox_intent_ref
  ON payos_rehearsal.webhook_inbox(payment_intent_id, provider_reference);
COMMIT;

-- All assertions are deliberately independent of data from the application.
DO $$
DECLARE
  i uuid := '00000000-0000-0000-0000-000000000101';
  j uuid := '00000000-0000-0000-0000-000000000102';
  bad uuid := '00000000-0000-0000-0000-000000000103';
  b uuid := '00000000-0000-0000-0000-000000000201';
  p uuid := '00000000-0000-0000-0000-000000000301';
  rejected boolean;
BEGIN
  INSERT INTO payos_rehearsal.payment_intents
  (id,building_id,payer_id,order_code,amount_vnd,payment_id,expires_at_utc,status)
  VALUES (i,b,p,900001,150000,'00000000-0000-0000-0000-000000000401',now() + interval '1 day',0),
         (j,b,p,900002,150000,'00000000-0000-0000-0000-000000000402',now() + interval '1 day',0);
  -- PostgreSQL UNIQUE NULL semantics permit multiple unmatched links.
  IF (SELECT count(*) FROM payos_rehearsal.payment_intents WHERE payment_link_id IS NULL) <> 2 THEN
    RAISE EXCEPTION 'NULL unique semantics changed';
  END IF;
  rejected := false;
  BEGIN
    INSERT INTO payos_rehearsal.payment_intents
    (id,building_id,payer_id,order_code,amount_vnd,payment_id,expires_at_utc,status)
    VALUES (bad,b,p,900001,150000,'00000000-0000-0000-0000-000000000403',now() + interval '1 day',0);
  EXCEPTION WHEN unique_violation THEN rejected := true;
  END;
  IF NOT rejected THEN RAISE EXCEPTION 'duplicate orderCode accepted'; END IF;

  rejected := false;
  BEGIN
    INSERT INTO payos_rehearsal.payment_intents
    (id,building_id,payer_id,order_code,amount_vnd,payment_id,expires_at_utc,status)
    VALUES (bad,b,p,900003,0,'00000000-0000-0000-0000-000000000403',now() + interval '1 day',0);
  EXCEPTION WHEN check_violation THEN rejected := true;
  END;
  IF NOT rejected THEN RAISE EXCEPTION 'zero amount accepted'; END IF;

  rejected := false;
  BEGIN
    INSERT INTO payos_rehearsal.payment_intents
    (id,building_id,payer_id,order_code,amount_vnd,expires_at_utc,status)
    VALUES (bad,b,p,900004,150000,now() + interval '1 day',0);
  EXCEPTION WHEN check_violation THEN rejected := true;
  END;
  IF NOT rejected THEN RAISE EXCEPTION 'missing payment target accepted'; END IF;

  UPDATE payos_rehearsal.payment_intents SET payment_link_id = 'LINK-A' WHERE id=i;
  rejected := false;
  BEGIN
    UPDATE payos_rehearsal.payment_intents SET payment_link_id='LINK-A' WHERE id=j;
  EXCEPTION WHEN unique_violation THEN rejected := true;
  END;
  IF NOT rejected THEN RAISE EXCEPTION 'duplicate paymentLinkId accepted'; END IF;

  rejected := false;
  BEGIN
    INSERT INTO payos_rehearsal.webhook_inbox
    (id,building_id,payment_intent_id,order_code,provider_reference,payload_sha256,received_at_utc,status)
    VALUES (bad,b,bad,900009,'TX-NO-INTENT',repeat('a',64),now(),0);
  EXCEPTION WHEN foreign_key_violation THEN rejected := true;
  END;
  IF NOT rejected THEN RAISE EXCEPTION 'orphan webhook accepted'; END IF;

  INSERT INTO payos_rehearsal.webhook_inbox
  (id,building_id,payment_intent_id,order_code,provider_reference,payload_sha256,received_at_utc,status)
  VALUES ('00000000-0000-0000-0000-000000000501',b,i,900001,'TX-ONE',repeat('a',64),now(),0);
  rejected := false;
  BEGIN
    INSERT INTO payos_rehearsal.webhook_inbox
    (id,building_id,payment_intent_id,order_code,provider_reference,payload_sha256,received_at_utc,status)
    VALUES ('00000000-0000-0000-0000-000000000502',b,j,900002,'TX-ONE',repeat('a',64),now(),0);
  EXCEPTION WHEN unique_violation THEN rejected := true;
  END;
  IF NOT rejected THEN RAISE EXCEPTION 'duplicate provider transaction accepted'; END IF;
  IF (SELECT count(*) FROM payos_rehearsal.webhook_inbox) <> 1 THEN
    RAISE EXCEPTION 'failed transaction left a webhook row';
  END IF;
  RAISE NOTICE 'PAYOS PG SCHEMA: POSITIVE + UNIQUE + FK + CHECK GATES PASS';
END $$;

-- Demonstrate that a valid first insert in one transaction gets rolled back
-- when a second duplicate insert fails; `psql` keeps overall script green.
DO $$
BEGIN
  BEGIN
    INSERT INTO payos_rehearsal.webhook_inbox
    (id,building_id,payment_intent_id,order_code,provider_reference,payload_sha256,received_at_utc,status)
    VALUES ('00000000-0000-0000-0000-000000000503',
            '00000000-0000-0000-0000-000000000201',
            '00000000-0000-0000-0000-000000000101',900001,'TX-ROLLBACK',repeat('b',64),now(),0);
    INSERT INTO payos_rehearsal.webhook_inbox
    (id,building_id,payment_intent_id,order_code,provider_reference,payload_sha256,received_at_utc,status)
    VALUES ('00000000-0000-0000-0000-000000000504',
            '00000000-0000-0000-0000-000000000201',
            '00000000-0000-0000-0000-000000000101',900001,'TX-ONE',repeat('b',64),now(),0);
    RAISE EXCEPTION 'duplicate unexpectedly succeeded';
  EXCEPTION WHEN unique_violation THEN
    NULL;
  END;
  IF EXISTS (SELECT 1 FROM payos_rehearsal.webhook_inbox WHERE provider_reference='TX-ROLLBACK') THEN
    RAISE EXCEPTION 'rollback lost atomicity';
  END IF;
  RAISE NOTICE 'PAYOS PG ROLLBACK: PASS';
END $$;

-- Bootstrap local: PostgreSQL + TimescaleDB para persistência assíncrona de FIX (cold path).
-- Aplicado automaticamente na primeira subida do volume (`docker-compose`).

CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS ingress_fix_messages (
    ingress_ts       TIMESTAMPTZ NOT NULL,
    session_id       TEXT        NOT NULL,
    msg_type         TEXT,
    cl_ord_id        TEXT,
    msg_seq_num      BIGINT,
    delivery_mode    TEXT        NOT NULL,
    dispatcher_ids   TEXT[],
    routing_sequence BIGINT,
    raw_fix          BYTEA,
    was_dropped      BOOLEAN     NOT NULL DEFAULT FALSE,
    drop_reason      TEXT
);

SELECT create_hypertable(
    'ingress_fix_messages',
    'ingress_ts',
    chunk_time_interval => INTERVAL '1 day',
    if_not_exists => TRUE
);

CREATE INDEX IF NOT EXISTS ix_ingress_fix_session_ts
    ON ingress_fix_messages (session_id, ingress_ts DESC);

CREATE INDEX IF NOT EXISTS ix_ingress_fix_clordid_ts
    ON ingress_fix_messages (cl_ord_id, ingress_ts DESC)
    WHERE cl_ord_id IS NOT NULL;

ALTER TABLE ingress_fix_messages SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'session_id',
    timescaledb.compress_orderby = 'ingress_ts DESC'
);

SELECT add_compression_policy('ingress_fix_messages', INTERVAL '7 days', if_not_exists => TRUE);
SELECT add_retention_policy('ingress_fix_messages', INTERVAL '90 days', if_not_exists => TRUE);

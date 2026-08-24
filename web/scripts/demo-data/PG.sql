-- The PostgreSQL side of the demo: the objects only PostgreSQL has, so the screenshots and the
-- smokes can show them. Seeded by the studio itself — see WDS_SEED_SQL.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE customers (
    id      serial PRIMARY KEY,
    name    text NOT NULL,
    city    text,
    country text NOT NULL
);

INSERT INTO customers (name, city, country) VALUES
    ('Ada Lovelace', 'London', 'GB'), ('Linus Pauling', 'Lisbon', 'PT'),
    ('Grace Hopper', 'New York', 'US'), ('Alan Turing', NULL, 'GB');

CREATE TABLE orders (
    id          serial PRIMARY KEY,
    customer_id int NOT NULL REFERENCES customers(id),
    total       numeric(10,2) NOT NULL,
    placed      timestamptz NOT NULL,
    status      text NOT NULL
);

INSERT INTO orders (customer_id, total, placed, status) VALUES
    (1, 149.50, '2026-06-01 09:12+00', 'shipped'),
    (1,  19.99, '2026-07-14 16:40+00', 'shipped'),
    (2, 245.00, '2026-07-22 11:05+00', 'open'),
    (3,  62.10, '2026-08-02 08:30+00', 'open'),
    (3, 512.75, '2026-08-11 19:55+00', 'refunded');

-- Row-level security, with a policy to show.
CREATE TABLE tenants (
    id        serial PRIMARY KEY,
    tenant_id int NOT NULL,
    note      text
);

INSERT INTO tenants (tenant_id, note) VALUES (1, 'first tenant'), (2, 'second tenant');

ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;

CREATE POLICY own_rows ON tenants FOR SELECT
    USING (tenant_id = current_setting('app.tenant', true)::int);

CREATE POLICY insert_own ON tenants FOR INSERT
    WITH CHECK (tenant_id = current_setting('app.tenant', true)::int);

-- A partitioned table, with partitions worth looking at.
CREATE TABLE events (
    id       bigserial,
    happened date NOT NULL,
    kind     text NOT NULL
) PARTITION BY RANGE (happened);

CREATE TABLE events_2026_06 PARTITION OF events FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');
CREATE TABLE events_2026_07 PARTITION OF events FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE events_2026_08 PARTITION OF events FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');

INSERT INTO events (happened, kind)
SELECT d::date, CASE WHEN random() < 0.5 THEN 'login' ELSE 'purchase' END
  FROM generate_series('2026-06-01'::date, '2026-08-30'::date, interval '1 day') d;

ANALYZE events;

-- A materialised view, to refresh.
CREATE MATERIALIZED VIEW order_totals AS
SELECT c.name, count(o.id) AS orders, coalesce(sum(o.total), 0) AS spent
  FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
 GROUP BY c.name;

-- A function to inspect: it raises a notice on the way, which is what the run shows.
CREATE FUNCTION spent_by(p_country text DEFAULT 'GB')
RETURNS numeric LANGUAGE plpgsql AS $$
DECLARE total numeric;
BEGIN
  RAISE NOTICE 'adding up orders for %', p_country;

  SELECT coalesce(sum(o.total), 0) INTO total
    FROM orders o JOIN customers c ON c.id = o.customer_id
   WHERE c.country = p_country;

  RETURN total;
END $$;

CREATE ROLE reporting NOLOGIN;
CREATE TYPE mood AS ENUM ('ok', 'bad');

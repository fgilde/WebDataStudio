-- The demo database the screenshots and the browser smokes run against. Seeded by the studio
-- itself: point WDS_SEED_SQL at this folder and a fresh file fills itself on start.
CREATE TABLE people (
    id        INTEGER PRIMARY KEY,
    name      TEXT NOT NULL,
    city      TEXT,
    email     TEXT,
    signed_up TEXT NOT NULL,
    active    INTEGER NOT NULL DEFAULT 1
);

INSERT INTO people (id, name, city, email, signed_up, active) VALUES
    (1, 'Ada Lovelace',    'london',   'ada@example.com',    '2026-01-14', 1),
    (2, 'Linus Pauling',   'lisbon',   'linus@example.com',  '2026-02-02', 1),
    (3, 'Grace Hopper',    'new york', 'grace@example.com',  '2026-02-19', 1),
    (4, 'Alan Turing',     'london',   'alan@example.com',   '2026-03-08', 0),
    (5, 'Barbara Liskov',  'boston',   'barbara@example.com','2026-04-21', 1),
    (6, 'Edsger Dijkstra', NULL,       'edsger@example.com', '2026-05-30', 1);

CREATE TABLE orders (
    id        INTEGER PRIMARY KEY,
    person_id INTEGER NOT NULL REFERENCES people(id),
    total     NUMERIC NOT NULL,
    placed    TEXT NOT NULL,
    status    TEXT NOT NULL
);

INSERT INTO orders (id, person_id, total, placed, status) VALUES
    (1, 1, 149.50, '2026-06-01 09:12:00', 'shipped'),
    (2, 1,  19.99, '2026-07-14 16:40:00', 'shipped'),
    (3, 2, 245.00, '2026-07-22 11:05:00', 'open'),
    (4, 3,  62.10, '2026-08-02 08:30:00', 'open'),
    (5, 3, 512.75, '2026-08-11 19:55:00', 'refunded'),
    (6, 4,   9.90, '2026-08-18 07:15:00', 'shipped'),
    (7, 5, 128.00, '2026-08-21 13:20:00', 'open');

CREATE TABLE items (
    id       INTEGER PRIMARY KEY,
    order_id INTEGER NOT NULL REFERENCES orders(id),
    label    TEXT NOT NULL,
    quantity INTEGER NOT NULL,
    price    NUMERIC NOT NULL
);

INSERT INTO items (id, order_id, label, quantity, price) VALUES
    (1, 1, 'Keyboard', 1, 129.00), (2, 1, 'Cable', 2, 10.25),
    (3, 3, 'Monitor', 1, 245.00),  (4, 4, 'Mouse', 2, 31.05),
    (5, 5, 'Chair', 1, 512.75),    (6, 7, 'Lamp', 4, 32.00);

CREATE INDEX ix_orders_person ON orders(person_id);

-- Geography, for the map view.
CREATE TABLE places (
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    lat  REAL NOT NULL,
    lon  REAL NOT NULL
);

INSERT INTO places (id, name, lat, lon) VALUES
    (1, 'London', 51.5072, -0.1276), (2, 'Lisbon', 38.7223, -9.1393),
    (3, 'New York', 40.7128, -74.0060), (4, 'Boston', 42.3601, -71.0589),
    (5, 'Reykjavik', 64.1466, -21.9426), (6, 'Berlin', 52.5200, 13.4050);

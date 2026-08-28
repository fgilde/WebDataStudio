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

-- A document column, for the JSON shape panel: the same paths in most rows, one row with an extra
-- one and one with a nested object, so the report has something to be honest about.
CREATE TABLE events (
    id        INTEGER PRIMARY KEY,
    person_id INTEGER REFERENCES people(id),
    kind      TEXT NOT NULL,
    payload   JSON NOT NULL,
    at        TEXT NOT NULL
);

INSERT INTO events (id, person_id, kind, payload, at) VALUES
    (1, 1, 'signup',   '{"plan":"pro","seats":4,"source":"web"}',                        '2026-01-14T09:12:00Z'),
    (2, 2, 'signup',   '{"plan":"free","seats":1,"source":"web"}',                       '2026-02-02T11:40:00Z'),
    (3, 1, 'upgrade',  '{"plan":"team","seats":12,"source":"sales","note":"call on friday"}', '2026-03-01T15:05:00Z'),
    (4, 3, 'signup',   '{"plan":"pro","seats":2,"source":"referral"}',                   '2026-02-19T08:20:00Z'),
    (5, 4, 'cancel',   '{"plan":"pro","seats":2,"reason":"price","refund":{"amount":49.5,"currency":"EUR"}}', '2026-04-02T17:55:00Z'),
    (6, 5, 'signup',   '{"plan":"pro","seats":3,"source":"web","tags":["beta","invited"]}', '2026-04-21T12:00:00Z');

CREATE INDEX ix_events_person ON events(person_id);

-- A wide table, for the data tab: fourteen columns and fourteen rows. The regression it guards is a
-- result whose rows rendered collapsed to nothing once a table had more columns than the grid
-- expected, so the shape of it matters more than what is in it.
CREATE TABLE Users (
    Id        INTEGER PRIMARY KEY,
    UserName  TEXT NOT NULL,
    Email     TEXT NOT NULL,
    City      TEXT,
    Country   TEXT,
    Phone     TEXT,
    Company   TEXT,
    Role      TEXT,
    Plan      TEXT,
    Locale    TEXT,
    TimeZone  TEXT,
    SignedUp  TEXT NOT NULL,
    LastSeen  TEXT,
    Active    INTEGER NOT NULL DEFAULT 1
);

INSERT INTO Users (Id, UserName, Email, City, Country, Phone, Company, Role, Plan, Locale, TimeZone, SignedUp, LastSeen, Active) VALUES
    (1,  'user1',  'user1@example.com',  'London',    'GB', '+44 20 7000 0001', 'Difference Engines', 'admin',  'pro',  'en-GB', 'Europe/London',   '2026-01-02', '2026-08-20', 1),
    (2,  'user2',  'user2@example.com',  'Lisbon',    'PT', '+351 21 000 0002', 'Azulejo Data',       'editor', 'free', 'pt-PT', 'Europe/Lisbon',   '2026-01-09', '2026-08-21', 1),
    (3,  'user3',  'user3@example.com',  'New York',  'US', '+1 212 000 0003',  'Hopper & Co',        'viewer', 'pro',  'en-US', 'America/New_York','2026-01-16', '2026-08-22', 1),
    (4,  'user4',  'user4@example.com',  'Boston',    'US', '+1 617 000 0004',  'Liskov Labs',        'editor', 'team', 'en-US', 'America/New_York','2026-01-23', '2026-08-19', 1),
    (5,  'user5',  'user5@example.com',  'Berlin',    'DE', '+49 30 000 0005',  'Turing Tools',       'viewer', 'free', 'de-DE', 'Europe/Berlin',   '2026-02-01', '2026-08-18', 0),
    (6,  'user6',  'user6@example.com',  'Vienna',    'AT', '+43 1 000 0006',   'Wirth Werke',        'editor', 'pro',  'de-AT', 'Europe/Vienna',   '2026-02-08', '2026-08-17', 1),
    (7,  'user7',  'user7@example.com',  'Zurich',    'CH', '+41 44 000 0007',  'Pascal Partners',    'admin',  'team', 'de-CH', 'Europe/Zurich',   '2026-02-15', '2026-08-16', 1),
    (8,  'user8',  'user8@example.com',  'Helsinki',  'FI', '+358 9 000 0008',  'Torvalds Trading',   'viewer', 'free', 'fi-FI', 'Europe/Helsinki', '2026-02-22', '2026-08-15', 1),
    (9,  'user9',  'user9@example.com',  'Nairobi',   'KE', '+254 20 000 0009', 'Savannah Systems',   'editor', 'pro',  'sw-KE', 'Africa/Nairobi',  '2026-03-01', '2026-08-14', 1),
    (10, 'user10', 'user10@example.com', 'Toronto',   'CA', '+1 416 000 0010',  'Maple Metrics',      'viewer', 'free', 'en-CA', 'America/Toronto', '2026-03-08', '2026-08-13', 0),
    (11, 'user11', 'user11@example.com', 'Hanoi',     'VN', '+84 24 000 0011',  'Red River Reports',  'editor', 'pro',  'vi-VN', 'Asia/Ho_Chi_Minh','2026-03-15', '2026-08-12', 1),
    (12, 'user12', 'user12@example.com', 'Tokyo',     'JP', '+81 3 000 0012',   'Sakura Storage',     'viewer', 'team', 'ja-JP', 'Asia/Tokyo',      '2026-03-22', '2026-08-11', 1),
    (13, 'user13', 'user13@example.com', 'Cape Town', 'ZA', '+27 21 000 0013',  'Table Mountain BI',  'admin',  'pro',  'en-ZA', 'Africa/Johannesburg','2026-03-29','2026-08-10', 1),
    (14, 'user14', 'user14@example.com', 'Reykjavik', 'IS', '+354 000 0014',    'Geysir Graphs',      'editor', 'free', 'is-IS', 'Atlantic/Reykjavik','2026-04-05','2026-08-09', 1);

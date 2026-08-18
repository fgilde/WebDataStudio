# Diagrams

![ER diagram](../assets/screenshots/diagram-dark.png)

The **Diagram** button draws the schema: one box per table with its columns, a key icon on the
primary key, a link icon on foreign-key columns, and an edge per relation labelled with the column
it joins on.

- The layout is automatic, left to right, so referenced tables sit left of the tables that point at
  them. Boxes can be dragged afterwards.
- The filter picks which tables to draw — the difference between a readable diagram and a wall of
  boxes on a schema with two hundred tables.
- The schema selector limits the diagram to one schema.
- Export writes an SVG or a PNG of what you see.
- Double-clicking a table's header opens its data.

The schema is read once and cached for a minute; the reload button bypasses the cache.

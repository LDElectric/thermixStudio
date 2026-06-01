import sqlite3
conn = sqlite3.connect('thermixStudioDB/thermix.db')
c = conn.cursor()
tables = [r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()]
print('Tables:', tables)
for t in tables:
    cols = [r[1] for r in c.execute(f"PRAGMA table_info({t})").fetchall()]
    rows = c.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0]
    print(f"  {t} ({rows} rows): {cols}")

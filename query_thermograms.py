import sqlite3, json
conn = sqlite3.connect('thermixStudioDB/thermix.db')
c = conn.cursor()
rows = c.execute("SELECT Id, FilePath, MetadataJson FROM Thermograms").fetchall()
for r in rows:
    meta = json.loads(r[2]) if r[2] else {}
    print(f"Id={r[0]}, FilePath={r[1]}")
    print(f"  MetadataKeys: {list(meta.keys())[:15]}")

using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace EzOdata.Showcase;

/// <summary>A deterministic, realistic dataset (same seed → same rows), identical in every service's database.</summary>
public static class Seed
{
    public const int Customers = 500;
    public const int Reps = 5;

    private static readonly string[] First = ["Ada", "Alan", "Grace", "Linus", "Margaret", "Dennis", "Barbara", "Ken", "Radia", "Tim", "Frances", "John", "Hedy", "Edsger", "Katherine", "Guido", "Anders", "Bjarne", "Donald", "Joan"];
    private static readonly string[] Last = ["Lovelace", "Turing", "Hopper", "Torvalds", "Hamilton", "Ritchie", "Liskov", "Thompson", "Perlman", "Berners-Lee", "Allen", "Backus", "Lamarr", "Dijkstra", "Johnson", "van Rossum", "Hejlsberg", "Stroustrup", "Knuth", "Clarke"];
    private static readonly string[] Countries = ["US", "US", "US", "CA", "GB", "DE", "FR", "NL", "SE", "JP", "BR", "IN"];
    private static readonly string[] Statuses = ["open", "paid", "paid", "paid", "shipped", "shipped", "cancelled"];
    private static readonly (string Category, string Name, double Price)[] Catalog =
    [
        ("Laptops", "Ultrabook 13", 1299), ("Laptops", "Workstation 16", 2499), ("Laptops", "Chromebook", 349),
        ("Monitors", "27in 4K", 499), ("Monitors", "34in Ultrawide", 799), ("Monitors", "24in FHD", 179),
        ("Keyboards", "Mechanical TKL", 129), ("Keyboards", "Split Ergo", 249), ("Keyboards", "Low-profile", 89),
        ("Audio", "Studio Headphones", 199), ("Audio", "USB Microphone", 149), ("Audio", "Earbuds", 99),
        ("Storage", "NVMe 2TB", 189), ("Storage", "Portable SSD 1TB", 119), ("Storage", "NAS 4-bay", 599),
        ("Accessories", "USB-C Dock", 229), ("Accessories", "Webcam 4K", 159), ("Accessories", "Laptop Stand", 49),
    ];

    public static void Create(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        c.Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, email TEXT UNIQUE,
                                    country TEXT NOT NULL, owner_id TEXT, created_by TEXT, is_deleted INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE products (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, category TEXT NOT NULL,
                                   price REAL NOT NULL, stock INTEGER NOT NULL);
            CREATE TABLE orders (id INTEGER PRIMARY KEY AUTOINCREMENT, customer_id INTEGER NOT NULL REFERENCES customers(id),
                                 status TEXT NOT NULL, ordered_at TEXT NOT NULL, total REAL NOT NULL);
            CREATE TABLE order_lines (order_id INTEGER NOT NULL REFERENCES orders(id), line_no INTEGER NOT NULL,
                                      product_id INTEGER NOT NULL REFERENCES products(id), qty INTEGER NOT NULL, unit_price REAL NOT NULL,
                                      PRIMARY KEY (order_id, line_no));
            CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, entity TEXT NOT NULL, entity_id INTEGER,
                                    action TEXT NOT NULL, actor TEXT);
            CREATE INDEX ix_orders_customer ON orders(customer_id);
            """);

        var rng = new Random(20260930);
        using var tx = c.BeginTransaction();
        foreach (var (category, name, price) in Catalog)
        {
            c.Execute("INSERT INTO products (name, category, price, stock) VALUES (@name, @category, @price, @stock)",
                new { name, category, price, stock = rng.Next(0, 250) }, tx);
        }

        for (var i = 1; i <= Customers; i++)
        {
            var name = $"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}";
            c.Execute("INSERT INTO customers (full_name, email, country, owner_id, created_by, is_deleted) VALUES (@name, @email, @country, @owner, 'seed', @deleted)",
                new { name, email = $"{name.ToLowerInvariant().Replace(' ', '.')}.{i}@example.com", country = Countries[rng.Next(Countries.Length)],
                      owner = $"rep-{(i - 1) % Reps + 1}", deleted = i % 50 == 0 ? 1 : 0 }, tx);
        }

        var start = new DateTime(2026, 1, 1);
        for (var customer = 1; customer <= Customers; customer++)
        {
            var orders = rng.Next(0, 8);
            for (var o = 0; o < orders; o++)
            {
                var lines = rng.Next(1, 5);
                var items = Enumerable.Range(1, lines).Select(n => (n, product: rng.Next(1, Catalog.Length + 1), qty: rng.Next(1, 4))).ToList();
                var total = items.Sum(x => Catalog[x.product - 1].Price * x.qty);
                var orderId = c.ExecuteScalar<long>(
                    "INSERT INTO orders (customer_id, status, ordered_at, total) VALUES (@customer, @status, @at, @total); SELECT last_insert_rowid();",
                    new { customer, status = Statuses[rng.Next(Statuses.Length)], at = start.AddDays(rng.Next(0, 270)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), total }, tx);
                foreach (var (n, product, qty) in items)
                {
                    c.Execute("INSERT INTO order_lines (order_id, line_no, product_id, qty, unit_price) VALUES (@orderId, @n, @product, @qty, @price)",
                        new { orderId, n, product, qty, price = Catalog[product - 1].Price }, tx);
                }
            }
        }

        tx.Commit();
    }
}

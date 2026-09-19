#:package Microsoft.Data.Sqlite@10.0.10
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Seeds the Error Dashboard demo fixture into the development database.
//
// Why this exists: the dashboards are unreadable when empty, and you cannot fill them by browsing
// the site. Network Error Logging only reports from Chromium, only over HTTPS, and reports arrive
// minutes late - and the collector rejects this fixture's host outright, because
// ReportHostValidator only accepts Umbraco's own configured domains. So the rows go into the
// database directly. They sit under the host "seeded.example" precisely so they can never be
// confused with, or collide with, genuine reports collected from the local site.
//
//   dotnet run seed.cs              seed (replaces any existing seeded.example rows)
//   dotnet run seed.cs -- --clear   remove the fixture and leave real reports alone
//
// Stop the site first: it holds the SQLite file and a write-ahead log.
//
// Dates are rebased on every run so the fixture is always "recent" - the charts cover a rolling
// window, and a fixture pinned to the day it was captured would silently fall off the end of them.
// Every value is bound as a DateTime parameter rather than written as a string literal, so the
// stored TEXT matches exactly what Umbraco's own writes produce and compares against.

// CallerFilePath is baked in at compile time, so this works wherever you invoke it from.
// AppContext.BaseDirectory would point at the temporary build output instead.
static string ScriptDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "")
    => Path.GetDirectoryName(path)!;

string here = ScriptDirectory();
string fixturePath = Path.Combine(here, "fixture.json");
if (!File.Exists(fixturePath))
{
    Console.Error.WriteLine($"fixture.json not found next to seed.cs (looked in {here})");
    return 1;
}

string databasePath = Path.GetFullPath(Path.Combine(here, "..", "..", "src", "Cms", "umbraco", "Data", "Umbraco.sqlite.db"));
if (!File.Exists(databasePath))
{
    Console.Error.WriteLine($"database not found at {databasePath} - run the site once to create it");
    return 1;
}

const string FixtureHost = "seeded.example";
bool clearOnly = args.Contains("--clear");

// The aggregation job rolls raw reports into the hourly table and remembers how far it got, as
// "<last report id>|<timestamp>" in umbracoKeyValue. This fixture seeds the raw reports *and* the
// hourly rows they roll up into, so the watermark has to be moved past them as well. Leave it
// behind and the next hourly run aggregates the seeded reports a second time, doubling every
// figure on the dashboards - and until that run happens the Overview reads "Not yet aggregated".
const string WatermarkKey = "ErrorDashboard.AggregationWatermark";

// Every DateTime column across the four tables. Anything named here gets rebased.
string[] dateColumns =
[
    "receivedUtc", "occurredUtc", "bucketUtc", "date", "firstSeenUtc", "lastSeenUtc",
    "raisedUtc", "windowStartUtc",
];

var tables = new (string Table, string Key)[]
{
    ("errorDashboardNelReport", "nelReport"),
    ("errorDashboardStatHourly", "statHourly"),
    ("errorDashboardStatUrlDaily", "statUrlDaily"),
    ("errorDashboardAlert", "alert"),
};

var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
using var db = new SqliteConnection(connectionString);
db.Open();

using (var transaction = db.BeginTransaction())
{
    foreach ((string table, _) in tables)
    {
        using var delete = db.CreateCommand();
        delete.CommandText = $"DELETE FROM {table} WHERE host = $host";
        delete.Parameters.AddWithValue("$host", FixtureHost);
        int removed = delete.ExecuteNonQuery();
        if (removed > 0)
        {
            Console.WriteLine($"{table}: removed {removed} existing {FixtureHost} rows");
        }
    }

    using (var forget = db.CreateCommand())
    {
        forget.CommandText = "DELETE FROM umbracoKeyValue WHERE \"key\" = $key";
        forget.Parameters.AddWithValue("$key", WatermarkKey);
        forget.ExecuteNonQuery();
    }

    transaction.Commit();
}

if (clearOnly)
{
    Console.WriteLine("\ncleared, including the aggregation watermark. Reports from other hosts were not touched.");
    return 0;
}

using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));

// The offset lands the fixture's most recent day on yesterday: recent enough for the rolling
// windows, entirely in the past so nothing is dated in the future.
DateTime latest = DateTime.MinValue;
foreach ((_, string key) in tables)
{
    foreach (JsonElement row in fixture.RootElement.GetProperty(key).EnumerateArray())
    {
        foreach (JsonProperty property in row.EnumerateObject())
        {
            if (dateColumns.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime value) && value > latest)
            {
                latest = value;
            }
        }
    }
}

var offset = TimeSpan.FromDays((DateTime.UtcNow.Date - latest.Date).Days - 1);
Console.WriteLine($"rebasing fixture by {offset.Days} days (latest event {latest:yyyy-MM-dd} -> {latest.Add(offset):yyyy-MM-dd})\n");

using (var transaction = db.BeginTransaction())
{
    foreach ((string table, string key) in tables)
    {
        JsonElement rows = fixture.RootElement.GetProperty(key);
        int inserted = 0;

        foreach (JsonElement row in rows.EnumerateArray())
        {
            var columns = row.EnumerateObject().Select(p => p.Name).ToArray();
            using var insert = db.CreateCommand();
            insert.CommandText =
                $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) " +
                $"VALUES ({string.Join(", ", columns.Select(c => "$" + c))})";

            foreach (JsonProperty property in row.EnumerateObject())
            {
                object? value = property.Value.ValueKind switch
                {
                    JsonValueKind.Null => DBNull.Value,
                    JsonValueKind.Number => property.Value.TryGetInt64(out long l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    _ => dateColumns.Contains(property.Name)
                        ? DateTime.Parse(property.Value.GetString()!, CultureInfo.InvariantCulture).Add(offset)
                        : property.Value.GetString(),
                };

                insert.Parameters.AddWithValue("$" + property.Name, value ?? DBNull.Value);
            }

            inserted += insert.ExecuteNonQuery();
        }

        Console.WriteLine($"{table}: inserted {inserted} rows");
    }

    // Move the watermark past everything just inserted, and date it to the end of the last hour the
    // seeded hourly rows cover, which is what the Overview shows as "last aggregated".
    long lastReportId;
    using (var max = db.CreateCommand())
    {
        max.CommandText = "SELECT COALESCE(MAX(id), 0) FROM errorDashboardNelReport";
        lastReportId = Convert.ToInt64(max.ExecuteScalar());
    }

    DateTime aggregatedTo = latest.Add(offset).AddHours(1);
    using (var mark = db.CreateCommand())
    {
        mark.CommandText =
            "INSERT OR REPLACE INTO umbracoKeyValue (\"key\", \"value\", \"updated\") " +
            "VALUES ($key, $value, $updated)";
        mark.Parameters.AddWithValue("$key", WatermarkKey);
        mark.Parameters.AddWithValue(
            "$value",
            string.Create(CultureInfo.InvariantCulture, $"{lastReportId}|{aggregatedTo:O}"));
        mark.Parameters.AddWithValue("$updated", DateTime.UtcNow);
        mark.ExecuteNonQuery();
    }

    Console.WriteLine($"aggregation watermark: report id {lastReportId}, aggregated to {aggregatedTo:u}");

    transaction.Commit();
}

Console.WriteLine($"""

    Seeded. Start the site and open Settings -> Error Dashboard.

      Overview     a fortnight of quiet baseline days, then a spike on the most recent day
      Error Pages  25 distinct paths on the spike day, so the table has a real second page
      Error Alerts 25 alert rows

    Remove it again with: dotnet run seed.cs -- --clear
    """);

return 0;

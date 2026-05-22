using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;

namespace MedicalApp.Services
{
    /// <summary>
    /// Settings for the LOINC seed - configured via <c>appsettings.json</c>
    /// in the <c>"Loinc"</c> section.
    /// </summary>
    public class LoincSettings
    {
        /// <summary>
        /// Path to the LOINC "Universal Lab Orders Value Set" CSV. Relative
        /// paths are resolved against the application's content root (the
        /// MedicalApp project folder). Default value mirrors the standard
        /// layout produced by unzipping <c>Loinc_2.82.zip</c> into
        /// <c>loinc_data/</c> at the repo root.
        /// </summary>
        public string UniversalLabOrdersCsvPath { get; set; } =
            "../loinc_data/Loinc_2.82/Loinc_2.82/AccessoryFiles/LoincUniversalLabOrdersValueSet/LoincUniversalLabOrdersValueSet.csv";
    }

    /// <summary>
    /// Idempotent loader for the LOINC dictionary. Runs at startup as part
    /// of <see cref="StartupSeed"/>. Behaviour:
    /// <list type="bullet">
    ///   <item>If the configured CSV file does NOT exist locally (e.g. fresh
    ///   clone, developer hasn't downloaded LOINC yet) -> log warning, leave
    ///   the table empty, app continues normally (LOINC mapping is opt-in).</item>
    ///   <item>If the table already has the same number of rows as the CSV
    ///   -> skip work (the seed already ran).</item>
    ///   <item>Otherwise, parse the CSV and bulk-insert/update the rows.</item>
    /// </list>
    /// The CSV format from LOINC has exactly 3 columns
    /// (<c>LOINC_NUM</c>, <c>LONG_COMMON_NAME</c>, <c>ORDER_OBS</c>) with
    /// double-quote escaping for fields containing commas.
    /// </summary>
    public static class LoincSeeder
    {
        public static async Task EnsureSeededAsync(
            IServiceProvider services,
            IWebHostEnvironment env,
            ILogger logger)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<LoincSettings>>().Value;

            // Resolve relative path against the content root (the MedicalApp/ folder).
            var configured = opts.UniversalLabOrdersCsvPath ?? string.Empty;
            var absolutePath = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));

            if (!File.Exists(absolutePath))
            {
                logger.LogWarning(
                    "LoincSeeder: CSV file not found at \"{Path}\". LOINC dictionary will be EMPTY. " +
                    "Download LOINC from https://loinc.org/downloads/ and place the archive contents " +
                    "according to the configured path. The application will continue without LOINC mapping.",
                    absolutePath);
                return;
            }

            // Parse the CSV first so we know how many entries we expect.
            List<LoincEntry> parsed;
            try
            {
                parsed = ParseUniversalLabOrdersCsv(absolutePath, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "LoincSeeder: failed to parse \"{Path}\". LOINC dictionary will remain unchanged.",
                    absolutePath);
                return;
            }

            if (parsed.Count == 0)
            {
                logger.LogWarning("LoincSeeder: parsed 0 entries from \"{Path}\". Nothing to seed.", absolutePath);
                return;
            }

            // Fast idempotency check: skip seeding when the table row count
            // is within 1% of the parsed CSV (tolerates the occasional row
            // skipped by the parser on malformed lines).
            var existingCount = await db.LoincDictionary.CountAsync();
            var tolerance = Math.Max(10, parsed.Count / 100);
            if (existingCount > 0 && Math.Abs(existingCount - parsed.Count) <= tolerance)
            {
                logger.LogInformation(
                    "LoincSeeder: dictionary already populated ({ExistingCount} entries, CSV has {ParsedCount}). Skipping.",
                    existingCount, parsed.Count);
                return;
            }

            // Re-seed strategy: when the table contents differ substantially from
            // the CSV (typically because the operator switched from a small subset
            // like "Universal Lab Orders" 1520-row to the full Loinc.csv 95k-row),
            // we TRUNCATE and re-load. This avoids stale rows from the previous
            // subset that aren't present in the new CSV.
            //
            // We use ExecuteSqlRawAsync("DELETE FROM ...") rather than TRUNCATE
            // because TRUNCATE requires DBO permissions which the runtime user
            // may not have. DELETE works under standard EF permissions.
            if (existingCount > 0)
            {
                logger.LogWarning(
                    "LoincSeeder: dictionary count mismatch (DB has {ExistingCount}, CSV has {ParsedCount}). " +
                    "Clearing and re-seeding from CSV.",
                    existingCount, parsed.Count);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM LoincDictionary");
            }

            // Bulk insert in batches. With ~95k rows from the full Loinc.csv,
            // EF Core change tracking would be slow if we added them all at once
            // (huge tracker, large transaction log). 1000-row batches keep memory
            // bounded and let SQL Server commit incrementally.
            var now = DateTime.UtcNow;
            const int batchSize = 1000;
            int insertedTotal = 0;

            for (int offset = 0; offset < parsed.Count; offset += batchSize)
            {
                var slice = parsed.Skip(offset).Take(batchSize).ToList();
                foreach (var row in slice) row.ImportedAt = now;
                db.LoincDictionary.AddRange(slice);
                await db.SaveChangesAsync();

                // Free the change tracker between batches - without this,
                // tracker memory grows linearly with total seeded rows.
                db.ChangeTracker.Clear();
                insertedTotal += slice.Count;

                // Progress log every 10k rows so the operator can see the seed working.
                if (insertedTotal % 10000 == 0 || insertedTotal == parsed.Count)
                {
                    logger.LogInformation(
                        "LoincSeeder: progress {Inserted}/{Total} rows inserted.",
                        insertedTotal, parsed.Count);
                }
            }

            logger.LogInformation(
                "LoincSeeder: dictionary refreshed from \"{Path}\". Inserted={Ins}, Total now={Tot}.",
                absolutePath, insertedTotal, await db.LoincDictionary.CountAsync());
        }

        // =====================================================================
        // CSV parsing
        // =====================================================================
        /// <summary>
        /// Parses a LOINC CSV (RFC 4180 compliant: comma separator, double-quote
        /// field delimiter, embedded quotes escaped as <c>""</c>, fields may
        /// contain embedded newlines inside quotes). Returns one
        /// <see cref="LoincEntry"/> per active row.
        /// Filters out rows whose STATUS column (if present) is NOT "ACTIVE".
        /// </summary>
        private static List<LoincEntry> ParseUniversalLabOrdersCsv(string path, ILogger logger)
        {
            var rows = new List<LoincEntry>();
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // Pull every logical "record" out of the file (a record is one row,
            // which may span multiple physical lines if it contains a quoted
            // field with embedded newlines). The full Loinc.csv DOES have such
            // rows in COMMENTS / RELATEDNAMES2 columns.
            var allRecords = ReadCsvRecords(reader);
            if (allRecords.Count == 0)
                throw new InvalidDataException("LOINC CSV is empty (no header row).");

            var header = allRecords[0];
            int idxCode = FindHeader(header, "LOINC_NUM");
            int idxName = FindHeader(header, "LONG_COMMON_NAME");
            int idxOrd = FindHeader(header, "ORDER_OBS");
            int idxStatus = FindHeader(header, "STATUS");
            // CLASS holds the LOINC specialty grouping (HEM/CHEM/SERO/ENDO/...).
            // Present in the full Loinc.csv release but NOT in the smaller
            // "Universal Lab Orders Value Set" subset — we handle both.
            int idxClass = FindHeader(header, "CLASS");

            if (idxCode < 0 || idxName < 0)
                throw new InvalidDataException(
                    "LOINC CSV header is missing required columns LOINC_NUM and/or LONG_COMMON_NAME.");

            int skippedInactive = 0;
            for (int r = 1; r < allRecords.Count; r++)
            {
                var cols = allRecords[r];
                if (cols.Count <= idxCode || cols.Count <= idxName) continue;
                var code = cols[idxCode].Trim();
                var name = cols[idxName].Trim();
                if (code.Length == 0 || name.Length == 0) continue;

                // Filter out non-ACTIVE rows. The full Loinc.csv exposes STATUS
                // values "ACTIVE", "DEPRECATED", "DISCOURAGED", "TRIAL". We
                // want ACTIVE only (everything else is either retired or
                // experimental and would only generate false positives).
                if (idxStatus >= 0 && cols.Count > idxStatus)
                {
                    var status = cols[idxStatus].Trim();
                    if (status.Length > 0 && !string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedInactive++;
                        continue;
                    }
                }

                rows.Add(new LoincEntry
                {
                    LoincCode = code,
                    LongCommonName = name.Length > 500 ? name[..500] : name,
                    OrderObs = (idxOrd >= 0 && cols.Count > idxOrd) ? cols[idxOrd].Trim() : null,
                    Class = (idxClass >= 0 && cols.Count > idxClass)
                        ? (cols[idxClass].Trim().Length > 0
                            ? (cols[idxClass].Trim().Length > 20
                                ? cols[idxClass].Trim()[..20]
                                : cols[idxClass].Trim())
                            : null)
                        : null
                });
            }

            logger.LogInformation(
                "LoincSeeder: parsed {Active} ACTIVE LOINC rows from CSV ({Inactive} non-ACTIVE skipped).",
                rows.Count, skippedInactive);

            return rows;
        }

        /// <summary>
        /// Reads a CSV stream and returns one List&lt;string&gt; per record.
        /// Handles fields that contain embedded newlines inside quotes
        /// (well-formed RFC 4180). The previous line-by-line implementation
        /// broke whenever a Loinc.csv comment contained a literal newline,
        /// silently truncating that row and shifting all column indices
        /// for subsequent rows.
        /// </summary>
        private static List<List<string>> ReadCsvRecords(StreamReader reader)
        {
            var records = new List<List<string>>();
            var current = new List<string>(40);
            var field = new StringBuilder(128);
            bool inQuotes = false;

            void CommitField() { current.Add(field.ToString()); field.Clear(); }
            void CommitRecord()
            {
                CommitField();
                records.Add(current);
                current = new List<string>(40);
            }

            int chInt;
            while ((chInt = reader.Read()) != -1)
            {
                char c = (char)chInt;

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        int next = reader.Peek();
                        if (next == '"')
                        {
                            reader.Read();      // consume the escaped quote
                            field.Append('"');
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else
                {
                    if (c == '"' && field.Length == 0)
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        CommitField();
                    }
                    else if (c == '\r')
                    {
                        // ignore - the LF that follows will commit the record
                    }
                    else if (c == '\n')
                    {
                        // End of record (only when we are not inside quotes).
                        CommitRecord();
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
            }

            // Flush trailing record (no terminating newline).
            if (field.Length > 0 || current.Count > 0)
                CommitRecord();

            return records;
        }

        private static int FindHeader(List<string> header, string name)
        {
            for (int i = 0; i < header.Count; i++)
                if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }
}

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

            // Fast idempotency check: if the table already has the exact same
            // number of rows as the parsed CSV, assume we are up-to-date.
            // (A finer-grained check by file hash is possible later if we
            // ever need to detect partial corruption.)
            var existingCount = await db.LoincDictionary.CountAsync();
            if (existingCount == parsed.Count)
            {
                logger.LogInformation(
                    "LoincSeeder: dictionary already populated ({Count} entries). Skipping.",
                    existingCount);
                return;
            }

            // Upsert: rows whose LoincCode already exists are updated, the rest
            // are inserted. We rely on EF Core change tracking with a small
            // batch transaction. ~1500 rows is well within a single transaction.
            var existingCodes = await db.LoincDictionary
                .Select(x => x.LoincCode)
                .ToListAsync();
            var existingSet = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            int inserted = 0, updated = 0;

            foreach (var row in parsed)
            {
                if (existingSet.Contains(row.LoincCode))
                {
                    var current = await db.LoincDictionary.FindAsync(row.LoincCode);
                    if (current != null)
                    {
                        // Only refresh the canonical fields; keep Aliases / Translations
                        // intact (those are populated by later steps, not by the seed).
                        current.LongCommonName = row.LongCommonName;
                        current.OrderObs = row.OrderObs;
                        current.ImportedAt = now;
                        updated++;
                    }
                }
                else
                {
                    row.ImportedAt = now;
                    db.LoincDictionary.Add(row);
                    inserted++;
                }
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "LoincSeeder: dictionary refreshed from \"{Path}\". Inserted={Ins}, Updated={Upd}, Total now={Tot}.",
                absolutePath, inserted, updated, await db.LoincDictionary.CountAsync());
        }

        // =====================================================================
        // CSV parsing
        // =====================================================================
        /// <summary>
        /// Parses LOINC's Universal Lab Orders CSV (RFC 4180 compliant: comma
        /// separator, double-quote field delimiter, embedded quotes escaped as
        /// <c>""</c>). Returns one <see cref="LoincEntry"/> per data row.
        /// </summary>
        private static List<LoincEntry> ParseUniversalLabOrdersCsv(string path, ILogger logger)
        {
            var rows = new List<LoincEntry>();
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // First line = header. Find column indexes so the parser is
            // resilient to column-order changes in future LOINC releases.
            var headerLine = reader.ReadLine();
            if (headerLine == null)
                throw new InvalidDataException("LOINC CSV is empty (no header row).");

            var header = SplitCsvLine(headerLine);
            int idxCode = FindHeader(header, "LOINC_NUM");
            int idxName = FindHeader(header, "LONG_COMMON_NAME");
            int idxOrd = FindHeader(header, "ORDER_OBS");

            if (idxCode < 0 || idxName < 0)
                throw new InvalidDataException(
                    "LOINC CSV header is missing required columns LOINC_NUM and/or LONG_COMMON_NAME.");

            int lineNumber = 1;
            while (!reader.EndOfStream)
            {
                lineNumber++;
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                List<string> cols;
                try
                {
                    cols = SplitCsvLine(line);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "LoincSeeder: skipping malformed line {Line}: \"{Snippet}\"",
                        lineNumber, line.Length > 120 ? line[..120] + "..." : line);
                    continue;
                }

                if (cols.Count <= idxCode || cols.Count <= idxName) continue;
                var code = cols[idxCode].Trim();
                var name = cols[idxName].Trim();
                if (code.Length == 0 || name.Length == 0) continue;

                rows.Add(new LoincEntry
                {
                    LoincCode = code,
                    LongCommonName = name.Length > 500 ? name[..500] : name,
                    OrderObs = (idxOrd >= 0 && cols.Count > idxOrd) ? cols[idxOrd].Trim() : null
                });
            }

            return rows;
        }

        /// <summary>
        /// Splits one RFC 4180-style CSV line into fields. Handles quoted
        /// fields and escaped quotes (<c>""</c> inside a quoted field
        /// produces a single literal quote).
        /// </summary>
        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>(8);
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Escaped quote ("") inside a quoted field -> literal '"'.
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == ',')
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    else if (c == '"' && sb.Length == 0)
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            result.Add(sb.ToString());
            return result;
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

using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// Completes <see cref="InterpretationResult.KeyResult.LoincClass"/> on
    /// every KeyResult whose <c>LoincCode</c> is set, by doing a single
    /// in-memory lookup against the <see cref="LoincEntry"/> table.
    ///
    /// Why this exists for CAM only:
    ///   * The B2C path runs an external Python LOINC matcher that fills both
    ///     the code and the class. The CAM module does NOT call Python — it
    ///     trusts whatever LOINC codes Gemini emits.
    ///   * Gemini frequently returns a correct <c>LoincCode</c> but leaves
    ///     <c>LoincClass</c> empty (or null). Without the class, the Compare
    ///     PDF cannot group rows by Hematology / Chemistry / etc. and ends
    ///     up dumping everything under "Alte analize".
    ///   * This service uses the existing <c>LoincDictionary</c> table (seeded
    ///     locally for B2C) as a lookup — zero external dependencies, one
    ///     batched query per CAM analysis.
    /// </summary>
    public class CamLoincClassEnricher
    {
        private readonly AppDbContext _db;

        public CamLoincClassEnricher(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Mutates <paramref name="result"/> in place: for each KeyResult that
        /// has a LoincCode but missing/empty LoincClass, fills it from the
        /// LoincDictionary table. KeyResults without a LoincCode are skipped.
        /// Never throws — best-effort enrichment.
        /// </summary>
        public async Task EnrichAsync(InterpretationResult result, CancellationToken ct = default)
        {
            if (result?.KeyResults == null || result.KeyResults.Count == 0) return;

            var codes = result.KeyResults
                .Where(k => !string.IsNullOrWhiteSpace(k.LoincCode)
                            && string.IsNullOrWhiteSpace(k.LoincClass))
                .Select(k => k.LoincCode!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (codes.Count == 0) return;

            var classByCode = await _db.LoincDictionary.AsNoTracking()
                .Where(le => codes.Contains(le.LoincCode))
                .Select(le => new { le.LoincCode, le.Class })
                .ToDictionaryAsync(x => x.LoincCode, x => x.Class, StringComparer.Ordinal, ct);

            foreach (var kr in result.KeyResults)
            {
                if (string.IsNullOrWhiteSpace(kr.LoincCode)) continue;
                if (!string.IsNullOrWhiteSpace(kr.LoincClass)) continue;
                if (classByCode.TryGetValue(kr.LoincCode.Trim(), out var cls) &&
                    !string.IsNullOrWhiteSpace(cls))
                {
                    kr.LoincClass = cls;
                }
            }
        }
    }
}

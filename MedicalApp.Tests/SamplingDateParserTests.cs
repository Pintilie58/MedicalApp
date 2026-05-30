using MedicalApp.Services;
using Xunit;

namespace MedicalApp.Tests
{
    /// <summary>
    /// Regression net pentru parsing-ul datei recoltării. Bug-ul istoric: în CAM
    /// Compare PDF data era data procesării (29 mai 2026) în loc de data reală
    /// din PDF (06.12.2023), pentru că <c>DateTime.TryParseExact</c> nu mătură
    /// șiruri tip "Data - ora recoltare: 06.12.2023 - 10:27". Acum acoperim
    /// dozele de format pe care Gemini le emite în practică.
    /// </summary>
    public class SamplingDateParserTests
    {
        [Theory]
        // Format românesc standard cu prefix label + oră (cazul Bordeianu).
        [InlineData("Data - ora recoltare: 06.12.2023 - 10:27", 2023, 12, 6)]
        [InlineData("06.12.2023 - 10:27", 2023, 12, 6)]
        // ISO (cazul Gemini când răspunde curat).
        [InlineData("2023-12-06", 2023, 12, 6)]
        [InlineData("2023-12-06T10:27:00", 2023, 12, 6)]
        // Slash + dash dd/mm/yyyy.
        [InlineData("06/12/2023", 2023, 12, 6)]
        [InlineData("06-12-2023", 2023, 12, 6)]
        [InlineData("6.12.2023", 2023, 12, 6)]
        // US (mm/dd/yyyy) cu month > 12 → trigger swap heuristic.
        [InlineData("01/27/2014", 2014, 1, 27)]
        // Named month — engleză.
        [InlineData("27 Jan 2014", 2014, 1, 27)]
        [InlineData("January 27, 2014", 2014, 1, 27)]
        // Named month — română (Gemini emite în română când limba = ro).
        [InlineData("6 decembrie 2023", 2023, 12, 6)]
        [InlineData("decembrie 6 2023", 2023, 12, 6)]
        // Two-digit year cu heuristic (year < 50 → 2000s).
        [InlineData("06/12/23", 2023, 12, 6)]
        public void TryParse_ValidFormats_ExtractsCorrectDate(string raw, int expectedYear, int expectedMonth, int expectedDay)
        {
            var result = SamplingDateParser.TryParse(raw);
            Assert.NotNull(result);
            Assert.Equal(expectedYear, result!.Value.Year);
            Assert.Equal(expectedMonth, result.Value.Month);
            Assert.Equal(expectedDay, result.Value.Day);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("    ")]
        [InlineData("nu există dată")]
        [InlineData("XX.YY.ZZZZ")]
        public void TryParse_InvalidInput_ReturnsNull(string? raw)
        {
            Assert.Null(SamplingDateParser.TryParse(raw));
        }
    }
}

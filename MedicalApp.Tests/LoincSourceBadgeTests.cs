using MedicalApp.Services;
using Xunit;

namespace MedicalApp.Tests
{
    /// <summary>
    /// Verifică contractul de afișare pentru sursa codului LOINC.
    /// Tabela e folosită atât în UI HTML (Compare cshtml) cât și în PDF
    /// (CamComparePdfGenerator). Un mismatch ar însemna ca operatorul vede
    /// culori diferite pentru aceeași sursă în două locuri.
    /// </summary>
    public class LoincSourceBadgeTests
    {
        [Theory]
        [InlineData("anchor", true)]
        [InlineData("semantic", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsVerified_OnlyAnchorSourceIsVerified(string? source, bool expected)
        {
            Assert.Equal(expected, LoincSourceBadge.IsVerified(source));
        }

        [Fact]
        public void GetPdfColor_AnchorAndSemantic_ReturnDifferentColors()
        {
            // Sanity check — verificarea că codul desenat e diferit pentru
            // verified vs auto, ca operatorul să le distingă în PDF.
            Assert.NotEqual(
                LoincSourceBadge.GetPdfColor("anchor"),
                LoincSourceBadge.GetPdfColor("semantic"));
        }
    }
}

using MedicalApp.Services;
using Xunit;

namespace MedicalApp.Tests
{
    /// <summary>
    /// Regression net pentru clasificarea valorilor în normal / borderline /
    /// high / low. Bug-ul istoric (Densitate urinară 1.024 ∈ [1.005, 1.03]
    /// marcată ↑) a apărut pentru că pe intervale înguste 5% din boundary
    /// value e o bandă uriașă proporțional cu range-ul. Fix-ul: când AMBELE
    /// limite sunt finite, tolerance = 5% din lățimea range-ului.
    /// Aceste tests acoperă bug-ul SPECIFIC + cazuri general.
    /// </summary>
    public class StatusValidatorTests
    {
        [Theory]
        // === Bug-ul Densitate urinară — cazul exact raportat de utilizator ===
        [InlineData(1.024, 1.005, 1.03, "normal")]      // mijlocul intervalului
        [InlineData(1.005, 1.005, 1.03, "borderline")]  // chiar pe limita inferioară
        [InlineData(1.030, 1.005, 1.03, "borderline")]  // chiar pe limita superioară
        [InlineData(1.04,  1.005, 1.03, "high")]        // peste interval
        [InlineData(1.000, 1.005, 1.03, "low")]         // sub interval

        // === Glucoză (range mediu, 70 - 100 mg/dL) ===
        [InlineData(85,   70, 100, "normal")]
        [InlineData(150,  70, 100, "high")]
        [InlineData(50,   70, 100, "low")]
        [InlineData(99,   70, 100, "borderline")] // 1% sub hi → 5% × width(30) = 1.5 band → 99 e borderline
        [InlineData(71,   70, 100, "borderline")] // 3.3% peste lo → în banda de 1.5 dela lo

        // === Hemoglobină (range 13.5 - 17.5 pentru bărbat) ===
        [InlineData(15.0, 13.5, 17.5, "normal")]
        [InlineData(13.5, 13.5, 17.5, "borderline")]
        [InlineData(18.0, 13.5, 17.5, "high")]
        public void ComputeStatus_FiniteRange_CorrectClassification(
            double value, double lo, double hi, string expected)
        {
            // Inclusive boundaries (cazul normal din rapoartele de laborator).
            var result = StatusValidator.ComputeStatus(value, lo, hi, loInc: true, hiInc: true);
            Assert.Equal(expected, result);
        }

        [Theory]
        // === Range deschis (Colesterol total < 200) — fallback la boundary-relative ===
        [InlineData(199, null, 200, "borderline")] // 0.5% sub 200 → borderline
        [InlineData(180, null, 200, "normal")]     // 10% sub 200 → normal
        [InlineData(220, null, 200, "high")]
        // === Range deschis în sus (ex. Fier > 50) ===
        [InlineData(45,  50, null, "low")]
        [InlineData(51,  50, null, "borderline")]  // 2% peste lo → borderline
        [InlineData(80,  50, null, "normal")]
        public void ComputeStatus_OpenEndedRange_CorrectClassification(
            double value, double? lo, double? hi, string expected)
        {
            var result = StatusValidator.ComputeStatus(value, lo, hi, loInc: true, hiInc: true);
            Assert.Equal(expected, result);
        }
    }
}

using MedicalApp.Models;
using MedicalApp.Services;

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

static KeyResult K(string p, string v, string u, string range, string status = "normal") => new KeyResult
{ Parameter = p, Value = v, Unit = u, ReferenceRange = range, Status = status };

static InterpretationResult R(params KeyResult[] krs) => new InterpretationResult
{ IsMedicalAnalysis = true, KeyResults = krs.ToList(), AbnormalFindings = new() };

const string Pdf = @"--- Page 1 ---
Laborator Synevo    Data recoltarii: 12-03-2024    Pacient: Ion Popescu
Hemoleucograma completa - Sange - Citometrie in flux
Leucocite (WBC)    6.5    10^3/uL    4.0 - 10.0 / 10^3/uL
Hemoglobina    14.2    g/dL    13.2 - 17.2 / g/dL    132 - 172 / g/L
Neutrofile    65.2 %    40 - 75 / %    4.24 10^3/uL    2 - 7.5 / 10^3/uL
Numar de bazofile    0.03    10^3/uL    0 - 0.1 / 10^3/uL
Procent de bazofile    0.4 %    0 - 0.2 / %
Procent de eozinofile    2.1 %    0.5 - 5 / %
Densitate urinara    1.024        1.005 - 1.030
Exces de baze    -1.5    mmol/L    -2 - 2 / mmol/L
Colesterol total    210    mg/dL    < 200 / mg/dL
Vitamina B-12    350    pg/mL    200 - 900 / pg/mL
Glicemie    95    mg/dL    70 - 100 / mg/dL    92
";

// ---- 1. Cazul raportat: 0 - 0.2 citit ca 0-2 ----
var r1 = R(K("Procent de bazofile", "0.4", "%", "0-2 / %"));
var c1 = ReferenceRangeVerifier.Verify(r1, Pdf);
Check("Bazofile: 1 corecție", c1.Count == 1, "n=" + c1.Count);
Check("Bazofile: interval devine 0-0.2 / %", r1.KeyResults![0].ReferenceRange == "0-0.2 / %", r1.KeyResults[0].ReferenceRange!);
StatusValidator.Validate(r1);
Check("Bazofile: status recalculat = high", r1.KeyResults[0].Status == "high", r1.KeyResults[0].Status);
int added = AbnormalFindingsCompleter.Complete(r1);
Check("Bazofile: apare în abnormal_findings", added == 1 && r1.AbnormalFindings!.Count == 1, "added=" + added);

// ---- 2. Interval corect → neatins ----
var r2 = R(K("Procent de bazofile", "0.4", "%", "0 - 0.2 / %", "high"));
Check("Interval corect: nicio corecție", ReferenceRangeVerifier.Verify(r2, Pdf).Count == 0);
Check("Interval corect: string neschimbat", r2.KeyResults![0].ReferenceRange == "0 - 0.2 / %");

// ---- 3. Două intervale pe rând (unități duble): modelul a ales unul valid ----
var r3 = R(K("Hemoglobina", "14.2", "g/dL", "13.2 - 17.2 / g/dL"), K("Hemoglobina", "14.2", "g/dL", "132-172"));
Check("Hb dublu-unit: nimic de corectat", ReferenceRangeVerifier.Verify(r3, Pdf).Count == 0);

// ---- 4. Corupere într-un rând cu două intervale înrudite (13.2-17.2 și 132-172) → ambiguu, neatins ----
var r4 = R(K("Hemoglobina", "14.2", "g/dL", "13-17 / g/dL"));
var c4 = ReferenceRangeVerifier.Verify(r4, Pdf);
Check("Hb 13-17 ambiguu (2 candidați înrudiți): neatins", c4.Count == 0 && r4.KeyResults![0].ReferenceRange == "13-17 / g/dL", r4.KeyResults![0].ReferenceRange!);

// ---- 5. Neutrofile: rând cu % și count, modelul cu 2-75 (zecimală pierdută) ----
var r5 = R(K("Neutrofile", "4.24", "10^3/uL", "2-75 / 10^3/uL"));
var c5 = ReferenceRangeVerifier.Verify(r5, Pdf);
Check("Neutrofile 2-75 → 2-7.5", c5.Count == 1 && r5.KeyResults![0].ReferenceRange == "2-7.5 / 10^3/uL", r5.KeyResults![0].ReferenceRange!);

// ---- 6. Modelul a pus un interval fără legătură (nu e corupere) → NU inventăm ----
var r6 = R(K("Procent de bazofile", "0.4", "%", "1-3 / %"));
Check("Interval nelegat 1-3: neatins (conservator)", ReferenceRangeVerifier.Verify(r6, Pdf).Count == 0 && r6.KeyResults![0].ReferenceRange == "1-3 / %");

// ---- 7. Prag '< 200' → ignorat de verificator ----
var r7 = R(K("Colesterol total", "210", "mg/dL", "< 200 / mg/dL", "high"));
Check("Prag <200: neatins", ReferenceRangeVerifier.Verify(r7, Pdf).Count == 0 && r7.KeyResults![0].ReferenceRange == "< 200 / mg/dL");

// ---- 8. Interval negativ (Exces de baze) ----
var r8 = R(K("Exces de baze", "-1.5", "mmol/L", "-2 - 2 / mmol/L"));
Check("Negativ -2 - 2: neatins", ReferenceRangeVerifier.Verify(r8, Pdf).Count == 0);
var r8b = R(K("Exces de baze", "-1.5", "mmol/L", "2 - 2 / mmol/L"));
ReferenceRangeVerifier.Verify(r8b, Pdf);
Check("Negativ: '2 - 2' (semn pierdut) → '-2 - 2' din PDF", r8b.KeyResults![0].ReferenceRange == "-2 - 2 / mmol/L", r8b.KeyResults![0].ReferenceRange!);

// ---- 9. Data 12-03-2024 și B-12 nu devin intervale ----
var r9 = R(K("Vitamina B-12", "350", "pg/mL", "200-900 / pg/mL"));
Check("B-12: neatins", ReferenceRangeVerifier.Verify(r9, Pdf).Count == 0);
var r9b = R(K("Vitamina B-12", "350", "pg/mL", "20-90 / pg/mL"));
var c9 = ReferenceRangeVerifier.Verify(r9b, Pdf);
Check("B-12: 20-90 → 200-900 (nu 12-03 din dată)", c9.Count == 1 && r9b.KeyResults![0].ReferenceRange == "200-900 / pg/mL", r9b.KeyResults![0].ReferenceRange!);

// ---- 10. Valoare precedentă în coloană (Glicemie ... 92) nu perturbă ----
var r10 = R(K("Glicemie", "95", "mg/dL", "70-100 / mg/dL"));
Check("Glicemie cu valoare anterioară: neatins", ReferenceRangeVerifier.Verify(r10, Pdf).Count == 0);

// ---- 11. Densitate 1.005 - 1.030 (model a scris 1.005-1.03 = numeric egal) ----
var r11 = R(K("Densitate urinara", "1.024", "", "1.005 - 1.03"));
Check("Densitate: egal numeric → neatins", ReferenceRangeVerifier.Verify(r11, Pdf).Count == 0);

// ---- 12. Parametru inexistent în text / mod vizual (fără text) ----
var r12 = R(K("TSH", "2.1", "uIU/mL", "0.4-4"));
Check("Parametru lipsă: neatins", ReferenceRangeVerifier.Verify(r12, Pdf).Count == 0);
Check("Text gol: neatins", ReferenceRangeVerifier.Verify(r12, "").Count == 0);
Check("Text extraction failed: neatins", ReferenceRangeVerifier.Verify(r12, "(text extraction failed - Gemini reads the PDF directly)").Count == 0);

// ---- 13. Valoarea 0.4 nu se confundă cu 0.03 / 10.4 (potrivire token întreg) ----
const string Pdf13 = "Bazofile    0.03    10^3/uL    0 - 0.1\nBazofile %    10.4    %    0 - 2\nBazofile    0.4    %    0 - 0.2\n";
var r13 = R(K("Bazofile", "0.4", "%", "0-2"));
var c13 = ReferenceRangeVerifier.Verify(r13, Pdf13);
Check("Token întreg: 0.4 alege rândul corect → 0-0.2", c13.Count == 1 && r13.KeyResults![0].ReferenceRange == "0-0.2", r13.KeyResults![0].ReferenceRange!);

// ---- 14. Rând duplicat (ambiguu) → neatins ----
const string Pdf14 = "Procent de bazofile 0.4 % 0 - 0.2\nProcent de bazofile 0.4 % 0 - 0.5\n";
var r14 = R(K("Procent de bazofile", "0.4", "%", "0-2"));
Check("Rând ambiguu: neatins", ReferenceRangeVerifier.Verify(r14, Pdf14).Count == 0);

// ---- 15. Virgulă decimală în PDF (0 - 0,2) ----
const string Pdf15 = "Procent de bazofile    0,4 %    0 - 0,2 / %\n";
var r15 = R(K("Procent de bazofile", "0.4", "%", "0-2 / %"));
var c15 = ReferenceRangeVerifier.Verify(r15, Pdf15);
Check("Virgulă: 0-2 → 0-0,2 (literal din PDF)", c15.Count == 1 && r15.KeyResults![0].ReferenceRange == "0-0,2 / %", r15.KeyResults![0].ReferenceRange!);
StatusValidator.Validate(r15);
Check("Virgulă: status high", r15.KeyResults[0].Status == "high", r15.KeyResults[0].Status);

// ---- 16. Nume cu marker de laborator în PDF (LLIS) și diacritice ----
const string Pdf16 = "LLIS Procent de bazofile    0.4 %    0 - 0.2 / %\n";
var r16 = R(K("Procent de bazofile", "0.4", "%", "0-2 / %"));
Check("Marker LLIS în PDF: găsit și corectat", ReferenceRangeVerifier.Verify(r16, Pdf16).Count == 1);

Console.WriteLine();
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILURE(S)");
return fails == 0 ? 0 : 1;

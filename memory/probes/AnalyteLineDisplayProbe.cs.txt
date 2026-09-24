using MedicalApp.Services;

int fails = 0;
void Check(string label, string? actual, string? expected)
{
    bool ok = actual == expected;
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + $"  ->  '{actual ?? "null"}'" + (ok ? "" : $"  (expected '{expected ?? "null"}')"));
    if (!ok) fails++;
}

// 1. Cazul raportat: linia completă reconstruită local → nimic descriptiv → ascuns
Check("Neutrofile segmentate 50 % 40 - 70 / %",
    AnalyteLineDisplay.Clean("Neutrofile segmentate 50 % 40 - 70 / %", "Neutrofile segmentate", "50", "%", "40 - 70 / %"), null);
Check("Eozinofile 10 % 1 - 4 / %",
    AnalyteLineDisplay.Clean("Eozinofile 10 % 1 - 4 / %", "Eozinofile", "10", "%", "1 - 4 / %"), null);
Check("Monocite 5 % 4 - 8 / %",
    AnalyteLineDisplay.Clean("Monocite 5 % 4 - 8 / %", "Monocite", "5", "%", "4 - 8 / %"), null);

// 2. Metadata reale emise de model → neschimbate
Check("-Ser - Turbidimetrie (ABX PENTRA C400 ISE)",
    AnalyteLineDisplay.Clean("-Ser - Turbidimetrie (ABX PENTRA C400 ISE)", "Creatinina serica", "0.932", "mg/dl", "0.5 - 1.3 / mg/dl"),
    "Ser - Turbidimetrie (ABX PENTRA C400 ISE)");
Check("Sange - Spectroscopie (PENTRA ES 60)",
    AnalyteLineDisplay.Clean("Sange - Spectroscopie de impedanta (PENTRA ES 60)", "Hemoglobina", "15.8", "g/dL", "12.6 - 17.4 / g/dL"),
    "Sange - Spectroscopie de impedanta (PENTRA ES 60)");

// 3. Linie completă CU metadata inline (Tip B) → rămâne doar metadata
Check("4. Creatinina serica -Ser - Spectrofotometrie (ABX PENTRA C400 ISE) 0.932 mg/dl 0.5 - 1.3 / mg/dl",
    AnalyteLineDisplay.Clean("4. Creatinina serica -Ser - Spectrofotometrie (ABX PENTRA C400 ISE)    0.932 mg/dl    0.5 - 1.3 / mg/dl",
        "Creatinina serica", "0.932", "mg/dl", "0.5 - 1.3 / mg/dl"),
    "Ser - Spectrofotometrie (ABX PENTRA C400 ISE)");

// 4. Prag "< 0.5" și diacritice / majuscule diferite în nume
Check("PROTEINA C REACTIVĂ 0.38 mg/dL < 0.5 / mg/dL",
    AnalyteLineDisplay.Clean("PROTEINA C REACTIVĂ    0.38 mg/dL    < 0.5 / mg/dL", "Proteina C reactiva", "0.38", "mg/dL", "< 0.5 / mg/dL"), null);

// 5. Virgulă decimală și interval fără spații
Check("Procent de bazofile 0,4 % 0-0,2/%",
    AnalyteLineDisplay.Clean("Procent de bazofile 0,4 % 0-0,2/%", "Procent de bazofile", "0.4", "%", "0-0.2 / %"), null);

// 6. Valoare urmată de flag și metadata la final
Check("Fibrinogenemie -Plasma - Coagulometrie (BFT II) 442.0 mg/dl 180.8 - 419.1 / mg/dl",
    AnalyteLineDisplay.Clean("3. Fibrinogenemie -Plasma - Coagulometrie (BFT II )    442.0 mg/dl    180.8 - 419.1 / mg/dl",
        "Fibrinogenemie", "442.0", "mg/dl", "180.8 - 419.1 / mg/dl"),
    "Plasma - Coagulometrie (BFT II )");

// 7. Gol / null
Check("null", AnalyteLineDisplay.Clean(null, "X", "1", "u", "0-1"), null);
Check("spații", AnalyteLineDisplay.Clean("   ", "X", "1", "u", "0-1"), null);

Console.WriteLine();
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILURE(S)");
return fails == 0 ? 0 : 1;

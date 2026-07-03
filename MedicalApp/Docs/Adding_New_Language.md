# Ghid: Cum adaugi o limbă nouă în MedicalApp+

> **Ultima actualizare:** februarie 2026
> **Motivația documentului:** După adăugarea limbii italiene am descoperit că nu era suficient să traducem `Loc.cs` — mai existau **3 locuri hardcodate** care produceau bug-uri (interpretarea Gemini genera EN pentru user IT). Acest checklist previne recurența.

---

## 📋 Checklist obligatoriu — 8 pași (~30-45 min per limbă)

### **PARTEA A — Infrastructura (5 fișiere)** — 5 min

- [ ] **1. `Services/Loc.cs`**
  - Adaugă `["xx"] = new() { }` (dict gol întâi, populat la pasul 6)

- [ ] **2. `Program.cs`**
  - Adaugă `new CultureInfo("xx")` în array-ul `supportedCultures`

- [ ] **3. `Views/Shared/_Layout.cshtml`**
  - Adaugă `'xx'` în array-ul JS `var supported = [...]` (auto-detect)

- [ ] **4. `Views/Home/Landing.cshtml`**
  - Adaugă tuplul `("xx", "\U000XXXXX\U000XXXXX", "NativeLangName")` în `langs[]`
  - Steag = 2× regional indicator codepoints (ex: 🇮🇹 = `\U0001F1EE\U0001F1F9`)

- [ ] **5. `Views/Home/Index.cshtml`**
  - Adaugă `<option value="xx" selected="@(currentCulture == "xx")">🇽🇽&nbsp; NativeLangName</option>`

### **PARTEA B — Gemini + OpenAI (3 fișiere) ⚠️ CRITIC** — 3 min

*Aceste locuri au FOST RATATE inițial și au produs bug-ul cu interpretarea în engleză pentru user italian.*

- [ ] **6. `Services/GeminiMedicalInterpretationService.cs` (linia ~30)**
  - Adaugă `["xx"] = "LanguageName (NativeName)"` în dict-ul static `LanguageNames`
  - Format standard: `"English"`, `"Romanian (Română)"`, `"Italian (Italiano)"`
  - **De ce e critic:** Placeholder-ul `{LANGUAGE_NAME}` din system prompt-ul Gemini folosește acest dict. Dacă lipsește → fallback pe "English" → Gemini generează interpretare în EN chiar dacă UI e în limba dorită.

- [ ] **7. `Services/MedicalInterpretationService.cs` (linia ~14)**
  - Copie identică a dict-ului (provider OpenAI, folosit ca fallback). Sincronizează cu cel de la #6.

- [ ] **8. `Services/CamBatchService.cs` (linia ~73, `SetBatchCultureAsync`)**
  - Adaugă `"xx" => "xx-XX"` în switch-ul `cultureName` (ex: `"it" => "it-IT"`)
  - **De ce e critic:** B2B batch-urile setează `CurrentUICulture` din acest switch. Fallback default e `"en-US"` → toate PDF-urile și emailurile din batch generate în EN.

### **PARTEA C — Traduceri Loc.cs (populare)** — 30-40 min

- [ ] **9. Traducere completă**
  - Sursă recomandată: **ES** (999 chei, master) — sau FR dacă limba nouă e mai apropiată de franceză
  - Foloseste script-ul Python din `/tmp/es_pairs.json` (regenerabil ușor)
  - Împarte în 4 chunks de 250 chei fiecare (evită context saturation)
  - Atenție la:
    - Format placeholders `{0}`, `{0:F1}%` — păstrează-le identic
    - Escape secvențe `\u00AB`, `\u201E` — folosește ghilimelele native ale limbii
    - Nume proprii de exemplu ("Juan García" → traducere culturală: "Giovanni Rossi" pentru IT)
    - Idiomuri de succes: `¡Buena suerte!` → `In bocca al lupo!` (IT), nu calc literal

### **PARTEA D — Validare** — 5 min

- [ ] **10. Structural check** (rulează în `/app/MedicalApp/`):
  ```bash
  python3 -c "
  import re
  with open('Services/Loc.cs', encoding='utf-8') as f: c = f.read()
  langs = re.findall(r'\[\"(en|ro|fr|es|de|xx)\"\] = new\(\)', c)
  print('Languages:', langs)
  # Should list all 6+ languages
  "
  ```

- [ ] **11. Local build** (VS2026 pe Windows):
  ```
  dotnet build MedicalApp.csproj
  ```
  Trebuie să compileze **fără erori** și **fără warning-uri noi**.

### **PARTEA E — Testare (fluxurile care ratau înainte)** — 10 min

- [ ] **12. Test B2C interpretare** (cel mai important test)
  - Setează UI pe limba nouă
  - Upload PDF de test
  - Verifică că **titlurile ȘI conținutul** interpretării sunt în limba nouă (nu mix)
  - Verifică raportul PDF descărcat — toate secțiunile în limba nouă

- [ ] **13. Test B2B batch** (dacă ai cont Clinic)
  - Setează UI clinic pe limba nouă
  - Pornește un batch cu 1-2 PDF
  - Verifică:
    - Log-ul live (`CamBatchLog*`) în limba nouă
    - Email-ul primit de pacient în limba nouă
    - PDF interpretare generat în limba nouă
    - PDF de comparație (dacă e cazul) în limba nouă
    - Sumar PDF/TXT al batch-ului în limba nouă

- [ ] **14. Test Admin → Translation Coverage**
  - Trebuie să apară `xx: 999/999 (100%)` (sau cifra corespunzătoare master-ului)

- [ ] **15. Test regresie**
  - Schimbă pe RO/EN/FR/ES/DE (una câte una) → totul funcționează normal

---

## 🔍 Cauza rădăcină istorică (educațional)

Când am adăugat italiana, Faza 1 a acoperit doar Partea A (5 fișiere). Am ratat Partea B (3 fișiere cu dicționare hardcodate). Simptomul:

| Layer | Ce s-a întâmplat pentru user IT |
|---|---|
| Cookie / RequestLocalization | Corect: cultura setată pe `it` |
| `Loc.T("XXX")` | Corect: returnează textul italian (Faza 2 populated) |
| `GeminiMedicalInterpretationService.LanguageNames["it"]` | ❌ Lipsă → fallback pe `"English"` |
| Prompt Gemini `{LANGUAGE_NAME}` → `"English"` | ❌ Gemini generează răspuns în EN |
| PDF-ul rezultat | ❌ **Mix**: titluri IT (din Loc), conținut EN (din Gemini) |

**Pentru B2B:** al doilea layer de bug — `CamBatchService.SetBatchCultureAsync` avea un switch cu fallback pe `"en-US"`, deci `CurrentUICulture` devenea EN pe toată durata batch-ului. Consecință: TOT era în EN (nu doar interpretarea).

---

## 🛡️ Recomandare pentru refactoring viitor (opțional, P3)

**Problemă structurală:** Avem **8 locuri diferite** unde trebuie sincronizată lista de limbi. Un dev nou care adaugă o limbă are șanse mari să rateze 1-2 dintre ele.

**Soluție ideală:** Un fișier unic de configurație (`SupportedLanguagesConfig.cs`):

```csharp
public static class SupportedLanguagesConfig
{
    public record LangDef(
        string Code,        // "it"
        string CultureCode, // "it-IT"
        string LangName,    // "Italian (Italiano)"  ← pentru Gemini
        string NativeName,  // "Italiano"            ← pentru UI
        string FlagEmoji    // "🇮🇹"
    );

    public static readonly IReadOnlyList<LangDef> All = new List<LangDef>
    {
        new("en", "en-US", "English",              "English",  "\U0001F1EC\U0001F1E7"),
        new("ro", "ro-RO", "Romanian (Română)",    "Română",   "\U0001F1F7\U0001F1F4"),
        new("fr", "fr-FR", "French (Français)",    "Français", "\U0001F1EB\U0001F1F7"),
        new("es", "es-ES", "Spanish (Español)",    "Español",  "\U0001F1EA\U0001F1F8"),
        new("de", "de-DE", "German (Deutsch)",     "Deutsch",  "\U0001F1E9\U0001F1EA"),
        new("it", "it-IT", "Italian (Italiano)",   "Italiano", "\U0001F1EE\U0001F1F9"),
    };
}
```

Toate cele 8 locuri hardcodate ar consuma această listă. Adăugarea unei limbi noi = **1 linie** în acest fișier + traducere în `Loc.cs`.

**Estimare efort:** 1-2 ore pentru refactoring inițial. Merită înainte de a mai adăuga alte limbi.

---

## 📎 Anexă — Locuri hardcodate identificate (Feb 2026)

| # | Fișier | Linia | Ce | Consecință dacă e ratat |
|---|---|---|---|---|
| 1 | `Services/Loc.cs` | ~5354 | `["it"] = new() { ... }` | Toate textele UI cad pe EN fallback |
| 2 | `Program.cs` | ~128 | `new CultureInfo("it")` | ASP.NET nu recunoaște cultura → 500 la SetLanguage |
| 3 | `Views/Shared/_Layout.cshtml` | ~120 | `supported = [...]` JS | Auto-detect nu setează cookie corect |
| 4 | `Views/Home/Landing.cshtml` | ~14 | `langs[]` tuple | Nu apare în dropdown-ul landing |
| 5 | `Views/Home/Index.cshtml` | ~24 | `<option>` | Nu apare în dropdown-ul Auth |
| 6 | `Services/GeminiMedicalInterpretationService.cs` | ~30 | `LanguageNames` dict | **⚠ Gemini generează interpretare în EN** |
| 7 | `Services/MedicalInterpretationService.cs` | ~14 | `LanguageNames` dict (OpenAI) | Fallback provider generează în EN |
| 8 | `Services/CamBatchService.cs` | ~73 | `cultureName` switch | **⚠ Batch B2B rulează în EN complet** |

---

*Odată aplicat acest checklist strict, adăugarea unei limbi noi devine deterministă și nu mai necesită zeci de teste.*

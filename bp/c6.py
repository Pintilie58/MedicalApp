from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.6. Graficul estimat al proiectului", 1)

    # ---------------- C.6.1 ----------------
    add_heading(doc, "C.6.1. Structura calendaristică generală", 2)
    add_para(doc,
             "Proiectul se desfășoară pe 18 luni de la semnarea "
             "contractului de finanțare, organizat în cele 11 etape "
             "descrise la C.4.4, cu suprapunere controlată pentru "
             "paralelizarea echipelor. Primele 3 luni sunt dedicate "
             "proiectării și fundației, lunile 3–12 dezvoltării intensive, "
             "iar lunile 12–18 consolidării, securizării, pilotării și "
             "lansării comerciale.")

    # ---------------- C.6.2 ----------------
    add_heading(doc, "C.6.2. Diagrama Gantt (formă tabelară)", 2)
    luni_header = ["Etapa"] + [str(i) for i in range(1, 19)]
    header = luni_header

    def bar(start, end):
        return ["█" if start <= i <= end else "" for i in range(1, 19)]

    rows = [
        ["E1 Analiză & proiectare"] + bar(1, 3),
        ["E2 Infrastructură & DevOps"] + bar(2, 4),
        ["E3 Nucleu platformă web"] + bar(3, 9),
        ["E4 Motor AI"] + bar(5, 11),
        ["E5 Microserviciu LOINC"] + bar(6, 12),
        ["E6 Raportare & comunicare"] + bar(8, 12),
        ["E7 Localizare 7→30 limbi"] + bar(8, 14),
        ["E8 Modul B2B (CAM)"] + bar(10, 15),
        ["E9 Monetizare & plăți"] + bar(11, 15),
        ["E10 Securitate & testare"] + bar(12, 16),
        ["E11 Pilotare & lansare"] + bar(14, 18),
    ]
    add_table(doc, headers=header, rows=rows,
              col_widths_cm=[4.6] + [0.65] * 18, font_size=7)
    add_para(doc, "Legendă: █ = luni active ale etapei.", italic=True, size=9)

    # ---------------- C.6.3 ----------------
    add_heading(doc, "C.6.3. Milestone-uri majore", 2)
    add_table(doc,
              headers=["Milestone", "Luna", "Criteriu de îndeplinire"],
              rows=[
                  ["M1 — Proiectare finalizată", "3",
                   "SRS, arhitectură, design system și corpus de validare "
                   "aprobate"],
                  ["M2 — Fundația tehnică operațională", "4",
                   "Medii IaC + CI/CD funcționale; release < 30 min"],
                  ["M3 — Landing page și waitlist live", "5",
                   "Pagina publică în 7 limbi; colectare înscrieri"],
                  ["M4 — Nucleu web complet", "9",
                   "Înregistrare, autentificare, profiluri, upload, "
                   "dashboard livrate și testate"],
                  ["M5 — Motor AI validat", "11",
                   "Completitudine ≥ 95% pe corpus; interpretare în 7 limbi"],
                  ["M6 — Codificare LOINC validată", "12",
                   "Precizie ≥ 96% pe 10.000 rapoarte; integrare end-to-end"],
                  ["M7 — MVP beta privat", "13",
                   "Flux complet funcțional pentru utilizatorii din waitlist"],
                  ["M8 — Modul B2B operațional", "15",
                   "Multi-tenant, batch, dashboard clinică, export FHIR"],
                  ["M9 — Plăți în producție", "15",
                   "Abonamente recurente active, facturare automată"],
                  ["M10 — Securitate certificată", "16",
                   "Pen-test remediat; DPIA aprobată; exercițiu DR reușit"],
                  ["M11 — 30 de limbi active", "18",
                   "Acoperire lingvistică completă pe toate canalele"],
                  ["M12 — Lansare comercială", "18",
                   "≥ 5.000 utilizatori; ≥ 10 contracte B2B; MRR ≥ 8.000 EUR"],
              ],
              col_widths_cm=[5.2, 1.4, 9.6])

    # ---------------- C.6.4 ----------------
    add_heading(doc, "C.6.4. Dependințe critice (drum critic)", 2)
    add_numbered(doc, [
        "E1 (specificații + corpus de validare) condiționează startul "
        "efectiv al E4 și E5 — fără corpus nu se poate măsura acuratețea;",
        "E2 (CI/CD) condiționează ritmul tuturor etapelor de dezvoltare;",
        "Faza 3.3 (autentificare) condiționează toate funcționalitățile "
        "per-utilizator (3.4–3.6, E8, E9);",
        "E4 (motor AI) și E5 (LOINC) condiționează împreună M7 (MVP): "
        "fluxul de valoare nu există fără ambele;",
        "E10 (securitate) condiționează lansarea comercială M12 — nu se "
        "lansează public fără pen-test remediat și DPIA aprobată."
    ])
    add_para(doc,
             "Drumul critic estimat: E1 → E2 → E3(3.3) → E4 → E5 → M7 → "
             "E10 → M12, cu rezervă totală de 6 săptămâni distribuită pe "
             "segmente.")

    # ---------------- C.6.5 ----------------
    add_heading(doc, "C.6.5. Contingență și rezerve temporale", 2)
    add_bullets(doc, [
        "Rezervă de timp: 6 săptămâni de buffer pe drumul critic (≈ 8% "
        "din durată), alocate prioritar etapelor E4–E5 (cel mai ridicat "
        "risc tehnologic);",
        "Strategie de descopere (de-scoping) controlată: în caz de "
        "întârziere majoră, extinderea 7→30 limbi (E7) și integrările "
        "HIS/LIS (faza 8.4) pot fi glisate post-lansare fără a afecta "
        "obiectivele specifice esențiale;",
        "Puncte de decizie go/no-go la M4, M6 și M10, cu planuri de "
        "acțiune predefinite;",
        "Raportare lunară de progres către finanțator, cu indicatori pe "
        "milestone-uri."
    ])

    # ---------------- C.6.6 ----------------
    add_heading(doc, "C.6.6. Planul de activități pe luni", 2)
    add_table(doc,
              headers=["Luna", "Activități principale"],
              rows=[
                  ["1", "Constituirea echipei; analiza cerințelor; startul "
                   "interviurilor cu medici și clinici; startul colectării "
                   "corpusului de validare"],
                  ["2", "Specificații funcționale (SRS); proiectarea "
                   "arhitecturii; provizionarea mediilor cloud (IaC)"],
                  ["3", "Finalizarea design system-ului și a machetelor "
                   "UX/UI; schema bazei de date; M1 — proiectare finalizată"],
                  ["4", "CI/CD complet (M2); startul dezvoltării nucleului "
                   "web; corpus de validare finalizat (10.000 documente)"],
                  ["5", "Landing page și waitlist live în 7 limbi (M3); "
                   "startul integrării modelului LLM multimodal"],
                  ["6", "Modulele de înregistrare și autentificare; startul "
                   "microserviciului LOINC (import nomenclator, index "
                   "semantic)"],
                  ["7", "Managementul profilurilor; iterații pe promptul "
                   "ierarhic și schema de răspuns strictă"],
                  ["8", "Modulul de upload cu deduplicare; pipeline-ul "
                   "hibrid LOINC (straturile semantic + fuzzy); startul "
                   "generatorului PDF și al e-mailurilor"],
                  ["9", "Dashboard-ul utilizatorului și istoricul (M4); "
                   "validatorul clinic determinist"],
                  ["10", "Auditorul de completitudine; stratul de reguli "
                   "contextuale și corecția unit-aware; startul modulului "
                   "B2B (multi-tenant)"],
                  ["11", "Interpretarea narativă în 7 limbi validată medical "
                   "(M5); startul sistemului de credite și abonamente"],
                  ["12", "Calibrarea finală LOINC pe corpus — precizie ≥ 96% "
                   "(M6); export FHIR; startul auditului de securitate"],
                  ["13", "MVP beta privat pentru waitlist (M7); procesarea "
                   "batch B2B; campanii pre-lansare"],
                  ["14", "Dashboard clinică și rapoarte comparative; "
                   "extinderea localizării către 30 de limbi; startul "
                   "programului pilot cu 5 clinici"],
                  ["15", "Integrarea gateway-ului de plăți în producție "
                   "(M9); API-ul B2B public; modulul B2B complet (M8)"],
                  ["16", "Remedierea constatărilor pen-test; DPIA aprobată; "
                   "exercițiu DR; teste de performanță (M10)"],
                  ["17", "Iterații pe feedback-ul pilotului; PR și studiu "
                   "național; pregătirea lansării comerciale"],
                  ["18", "30 de limbi active (M11); lansarea comercială "
                   "B2C + B2B (M12); raportul final al proiectului"],
              ],
              col_widths_cm=[1.4, 14.8], font_size=9)
    page_break(doc)

from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.9. Evaluarea și atenuarea riscurilor", 1)

    # ---------------- C.9.1 ----------------
    add_heading(doc, "C.9.1. Metodologia de management al riscului", 2)
    add_para(doc,
             "Riscurile vor fi gestionate conform unui proces formal, "
             "aliniat ISO 31000: identificare continuă (registru de "
             "riscuri actualizat lunar), evaluare pe matrice probabilitate "
             "× impact (scale 1–5), planuri de răspuns per risc "
             "(evitare / atenuare / transfer / acceptare), proprietar "
             "desemnat pentru fiecare risc și revizuire în ședința lunară "
             "de proiect. Riscurile cu scor ≥ 12 se raportează imediat "
             "managementului și, unde este cazul, finanțatorului.")

    # ---------------- C.9.2 ----------------
    add_heading(doc, "C.9.2. Registrul riscurilor identificate", 2)
    add_table(doc,
              headers=["Risc", "P", "I", "Scor", "Măsuri de atenuare"],
              rows=[
                  ["R1 — Tehnologic: acuratețea extragerii AI sub ținta de "
                   "95% pe formate rare de buletine",
                   "3", "5", "15",
                   "Corpus de validare divers (30+ laboratoare) din luna 3; "
                   "dublu canal PDF+text; escaladare adaptivă; auditor de "
                   "completitudine cu reprocesare; buffer pe drumul critic"],
                  ["R2 — Tehnologic: precizia codificării LOINC sub 96% pe "
                   "denumiri atipice",
                   "3", "4", "12",
                   "Pipeline în 4 straturi cu ancore deterministe pe "
                   "analiții frecvenți; suită de regresie cu gard de "
                   "calitate; validare medicală a dicționarului de ancore"],
                  ["R3 — Dependență de furnizorul de modele AI (preț, "
                   "disponibilitate, politici)",
                   "2", "4", "8",
                   "Strat de abstractizare peste API-ul LLM; compatibilitate "
                   "testată cu minimum 2 furnizori; monitorizarea costului "
                   "per document cu alerte"],
                  ["R4 — Reglementar: încadrare mai strictă în AI Act / "
                   "MDR decât cea anticipată",
                   "2", "5", "10",
                   "Consultanță juridică specializată din etapa E1; "
                   "poziționare fără diagnostice; validare deterministă și "
                   "jurnalizare completă; monitorizarea ghidurilor "
                   "autorităților"],
                  ["R5 — GDPR: incident de securitate cu date de sănătate",
                   "2", "5", "10",
                   "Security by design (C.4.7); pen-test extern; criptare "
                   "end-to-end; DPIA; plan de răspuns la incidente cu "
                   "notificare în 72h; asigurare cyber"],
                  ["R6 — Resurse umane: dificultăți de recrutare / plecarea "
                   "unor specialiști-cheie",
                   "3", "3", "9",
                   "Pachete salariale competitive; documentare obligatorie "
                   "(bus factor ≥ 2 pe fiecare componentă); parteneriate cu "
                   "universități; subcontractare de rezervă"],
                  ["R7 — Comercial: adopție B2C sub așteptări",
                   "3", "4", "12",
                   "Validarea cererii înainte de dezvoltarea completă "
                   "(C.3.3); waitlist ≥ 3.000; freemium cu fricțiune "
                   "minimă; pivotare buget spre canalele cu CAC dovedit"],
                  ["R8 — Comercial: ciclu de vânzare B2B mai lung decât "
                   "estimat",
                   "4", "3", "12",
                   "Program pilot cu 5 clinici din luna 14; scrisori de "
                   "intenție; self-service pentru tierele mici; parteneriate "
                   "cu laboratoare ca multiplicator"],
                  ["R9 — Financiar: depășirea bugetului de dezvoltare",
                   "2", "4", "8",
                   "Rezervă de contingență; monitorizare lunară "
                   "cost-per-etapă; descopere controlată (E7/8.4 glisabile "
                   "post-lansare)"],
                  ["R10 — Concurențial: intrarea unui jucător mare pe nișă",
                   "2", "3", "6",
                   "Viteză de execuție (first-mover local); bariere tehnice "
                   "(pipeline hibrid, 30 limbi); contracte B2B multi-anuale; "
                   "focus pe piețe/limbi neglijate de jucătorii globali"],
              ],
              col_widths_cm=[5.6, 0.8, 0.8, 1.1, 7.9],
              font_size=9)
    add_para(doc, "P = probabilitate (1–5); I = impact (1–5); "
             "Scor = P × I.", italic=True, size=9)

    add_para(doc, "Matricea de poziționare a riscurilor (probabilitate × impact):", bold=True)
    add_table(doc,
              headers=["P \\ I", "1 — Neglijabil", "2 — Minor", "3 — Moderat",
                       "4 — Major", "5 — Critic"],
              rows=[
                  ["5 — Aproape sigur", "", "", "", "", ""],
                  ["4 — Probabil", "", "", "R8", "", ""],
                  ["3 — Posibil", "", "", "R6", "R2, R7", "R1"],
                  ["2 — Puțin probabil", "", "", "R10", "R3, R9", "R4, R5"],
                  ["1 — Rar", "", "", "", "", ""],
              ],
              col_widths_cm=[3.6, 2.5, 2.5, 2.5, 2.5, 2.5], font_size=9)
    add_para(doc,
             "Niciun risc nu se situează în zona inacceptabilă (scor ≥ 20); "
             "riscurile din banda 12–15 (R1, R2, R7, R8) au măsuri de "
             "atenuare active încă din proiectarea soluției și puncte de "
             "verificare pe milestone-uri.", italic=True)

    # ---------------- C.9.3 ----------------
    add_heading(doc, "C.9.3. Toleranța la risc și pragurile de escaladare", 2)
    add_bullets(doc, [
        "Riscuri tehnologice (R1–R3): toleranță medie — inovația implică "
        "incertitudine; gestionate prin măsurare obiectivă continuă pe "
        "corpusul de validare și puncte go/no-go la M4/M6/M10;",
        "Riscuri de conformitate (R4–R5): toleranță zero — nicio lansare "
        "fără DPIA aprobată și pen-test remediat;",
        "Riscuri comerciale (R7–R8): toleranță controlată — bugetele de "
        "achiziție se realocă lunar către canalele cu randament dovedit;",
        "Prag de escaladare către management: scor ≥ 12 sau orice risc "
        "nou de conformitate."
    ])

    # ---------------- C.9.4 ----------------
    add_heading(doc, "C.9.4. Continuitatea afacerii (Business Continuity)", 2)
    add_bullets(doc, [
        "Backup automat al bazei de date la 6 ore, retenție 35 de zile, "
        "test de restaurare trimestrial;",
        "Obiective de recuperare: RPO 1 oră, RTO 4 ore, exercițiu de "
        "dezastru documentat înainte de lansare (faza 10.3);",
        "Degradare elegantă: în caz de indisponibilitate a furnizorului "
        "AI, platforma acceptă documente în coadă și procesează la "
        "restabilire, cu informarea transparentă a utilizatorilor;",
        "Plan de comunicare de criză și pagină de status public pentru "
        "clienții B2B (SLA)."
    ])
    add_para(doc,
             "Concluzie: profilul de risc al proiectului este tipic unui "
             "proiect greenfield de inovare digitală, cu riscuri "
             "identificate, măsurabile și acoperite prin însăși "
             "arhitectura soluției (straturi deterministe peste AI), prin "
             "metodologia de implementare (validare timpurie, milestone-uri "
             "go/no-go, buffere) și prin bugetul de contingență. Niciun "
             "risc identificat nu are caracter blocant pentru fezabilitatea "
             "proiectului.", bold=True)

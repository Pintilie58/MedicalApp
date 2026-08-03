from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    # ---------------- C.4.5 ----------------
    add_heading(doc, "C.4.5. Fluxul de date al platformei (data flow)", 2)
    add_para(doc,
             "Fluxul complet de procesare a unui buletin de analize, așa "
             "cum va fi implementat, parcurge următorii pași (tranzițiile "
             "sincron/asincron marcate explicit):")
    add_numbered(doc, [
        "Utilizatorul autentificat încarcă documentul PDF prin interfața "
        "web; validare pe client (dimensiune, tip) înainte de transmitere;",
        "Serverul validează suplimentar (dimensiune ≤ 10 MB, tip MIME, "
        "semnătura binară %PDF-), verifică soldul de credite și "
        "calculează amprenta SHA-256 pentru deduplicare;",
        "Dacă amprenta există deja → utilizatorul este informat și poate "
        "alege reprocesarea contra cost; dacă este nouă → continuă;",
        "Serviciul de interpretare AI transmite documentul pe dublu canal "
        "(PDF vizual + text extras) către modelul LLM multimodal, cu "
        "schema strictă de răspuns;",
        "Răspunsul JSON este validat structural; la eșec se aplică "
        "repararea tolerantă, apoi escaladarea către modelul superior;",
        "Validatorul clinic determinist recalculează statusul fiecărui "
        "parametru; auditorul de completitudine verifică pragul de 95%;",
        "Clientul de codificare apelează în paralel microserviciul LOINC "
        "pentru fiecare analit; codurile, scorurile și sursele deciziilor "
        "sunt atașate rezultatelor;",
        "Rezultatul final este persistat (JSON complet + rezultate-cheie "
        "denormalizate + metadate de consum AI);",
        "Generatorul PDF produce raportul final (sincron, sub 500 ms);",
        "Notificarea e-mail se trimite asincron, fără blocarea "
        "utilizatorului;",
        "Utilizatorul este redirecționat către dashboard, unde raportul "
        "este disponibil imediat pentru vizualizare și descărcare."
    ])

    # ---------------- C.4.6 ----------------
    add_heading(doc, "C.4.6. Specificații funcționale și non-funcționale", 2)
    add_para(doc, "Cerințe funcționale principale (FR):", bold=True)
    add_bullets(doc, [
        "FR-01: Înregistrare cu verificare e-mail și consimțământ GDPR "
        "granular; autentificare cu remember-me și resetare parolă;",
        "FR-02: Încărcare PDF (max 10 MB) cu deduplicare SHA-256 și "
        "gestionare credite;",
        "FR-03: Extragere structurată automată cu completitudine ≥ 95% "
        "față de conținutul documentului;",
        "FR-04: Codificare LOINC automată cu precizie ≥ 96% pe eșantionul "
        "de validare de 10.000 rapoarte;",
        "FR-05: Interpretare narativă în limba pacientului (7 limbi la "
        "MVP, 30 la finalul proiectului);",
        "FR-06: Raport PDF cu grupare pe panele, statusuri vizuale și cod "
        "LOINC per analit;",
        "FR-07: Istoric per profil de pacient (max 5 profiluri premium) "
        "și evoluție grafică multi-vizită pe același parametru;",
        "FR-08: Trimiterea raportului prin e-mail;",
        "FR-09: Export HL7 FHIR (Observation, DiagnosticReport, Patient);",
        "FR-10: Modul B2B multi-tenant cu procesare batch, dashboard și "
        "facturare per volum;",
        "FR-11: Plăți recurente (abonamente B2C/B2B) cu facturare automată."
    ])
    add_para(doc, "Cerințe non-funcționale (NFR):", bold=True)
    add_bullets(doc, [
        "NFR-01: Disponibilitate țintă 99,9% (SLA B2B), infrastructură "
        "redundantă;",
        "NFR-02: Timp de răspuns end-to-end < 15 secunde per raport "
        "(percentila 95), < 8 secunde median;",
        "NFR-03: Scalare orizontală automată la vârfuri de trafic;",
        "NFR-04: Securitate — TLS 1.3, secrete în seif dedicat, parole "
        "Argon2id, limitare de rată per IP, protecție CSRF/XSS;",
        "NFR-05: Conformitate GDPR — DPIA, drept la ștergere "
        "(soft-delete 30 zile + hard-delete), export DSAR ≤ 30 zile;",
        "NFR-06: Jurnalizare centralizată — 90 zile operațional, 7 ani "
        "audit medical;",
        "NFR-07: Backup automat al bazei de date la 6 ore, retenție 35 "
        "zile;",
        "NFR-08: Recuperare în caz de dezastru — RPO 1 oră, RTO 4 ore;",
        "NFR-09: Accesibilitate WCAG 2.1 nivel AA;",
        "NFR-10: Localizare completă (UI, interpretare, PDF, e-mail, "
        "erori) pentru toate limbile active."
    ])

    # ---------------- C.4.7 ----------------
    add_heading(doc, "C.4.7. Securitatea cibernetică a soluției", 2)
    add_para(doc,
             "Securitatea va fi proiectată de la început (security by "
             "design), conform ISO/IEC 27001:2022 și OWASP Top 10:")
    add_bullets(doc, [
        "Autentificare: parole cu hash Argon2id (parametri memory-hard), "
        "sesiuni securizate cu rotație, fundație MFA (TOTP), blocare "
        "progresivă anti brute-force;",
        "Autorizare: control de acces pe roluri ierarhice (utilizator, "
        "premium, administrator clinică, operator, suport, administrator "
        "platformă) și izolare de tenant pentru B2B;",
        "Date în tranzit: TLS 1.3 obligatoriu, HSTS, certificate "
        "reînnoite automat;",
        "Date la repaus: criptare transparentă a bazei de date (TDE), "
        "criptarea stocării de obiecte, secrete exclusiv în seiful "
        "dedicat (fără chei în cod sau configurații);",
        "Protecții aplicative: interogări parametrizate (anti SQL "
        "injection), auto-encoding și Content Security Policy strictă "
        "(anti XSS), token anti-CSRF, validarea tuturor intrărilor, "
        "limitare de rată;",
        "Audit: jurnalizarea acțiunilor sensibile cu retenție 7 ani;",
        "Monitorizare: alerte automate pentru tipare anormale "
        "(brute-force, anomalii geografice, rafale de upload)."
    ])

    # ---------------- C.4.8 ----------------
    add_heading(doc, "C.4.8. Integrările externe planificate", 2)
    add_table(doc,
              headers=["Integrare", "Rol", "Observații"],
              rows=[
                  ["API LLM multimodal (familia Google Gemini)",
                   "Extragerea structurată din PDF și interpretarea narativă",
                   "Apel REST cu schemă strictă de răspuns; politici de "
                   "reîncercare; escaladare adaptivă între modele"],
                  ["Nomenclatorul LOINC (Regenstrief Institute)",
                   "Baza terminologică a codificării",
                   "Licențiere conform termenilor oficiali; actualizări "
                   "semestriale versionate"],
                  ["Relay SMTP tranzacțional",
                   "E-mailuri de sistem (verificare, resetare, rapoarte)",
                   "Trimitere asincronă; domeniu dedicat cu SPF/DKIM/DMARC"],
                  ["Gateway de plăți european",
                   "Abonamente recurente B2C/B2B, SCA/3-D Secure",
                   "Facturare automată, TVA OSS pentru vânzări UE"],
                  ["Seif de secrete și telemetrie cloud",
                   "Gestiunea credențialelor și observabilitate",
                   "Identități gestionate, fără secrete în cod"],
                  ["HL7 FHIR (conectori B2B)",
                   "Interoperabilitate cu HIS/LIS și pregătire EHDS",
                   "Resurse Observation/DiagnosticReport/Patient cu coduri "
                   "LOINC"],
              ],
              col_widths_cm=[4.6, 5.4, 6.2])

    # ---------------- C.4.9 ----------------
    add_heading(doc, "C.4.9. Metodologia de dezvoltare și asigurarea calității", 2)
    add_para(doc,
             "Dezvoltarea va urma metodologia Agile-Scrum cu sprinturi de "
             "două săptămâni, planificare pe releases aliniate etapelor "
             "E1–E11, demonstrații la finalul fiecărui sprint și "
             "retrospective. Asigurarea calității este integrată în "
             "procesul de livrare:")
    add_bullets(doc, [
        "Piramidă de teste: unitare (țintă ≥ 70% acoperire pe nucleul de "
        "business), de integrare (fluxuri API end-to-end), de interfață "
        "(scenarii critice automatizate) și suita de regresie a "
        "acurateței LOINC/AI rulată pe corpusul de validare;",
        "Revizuire de cod obligatorie (four-eyes) pentru fiecare "
        "modificare; analiză statică și scanare de dependențe în CI;",
        "Definition of Done formalizată: cod revizuit, teste trecute, "
        "documentație actualizată, criterii de acceptanță ale fazei "
        "îndeplinite;",
        "Gestiunea versiunilor semantică și jurnal de modificări per "
        "release;",
        "Managementul configurației prin infrastructură-sub-formă-de-cod, "
        "cu medii reproductibile."
    ])
    add_para(doc,
             "Trasabilitatea cerințelor va fi menținută pe întreg lanțul "
             "specificație → fază de dezvoltare → test → criteriu de "
             "acceptanță, astfel încât fiecare cerință funcțională din "
             "C.4.6 să fie verificabilă la recepția etapei corespunzătoare.")
    page_break(doc)

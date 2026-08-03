from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def _faza(doc, titlu, descriere, livrabile, criterii=None):
    add_para(doc, titlu, bold=True)
    add_para(doc, descriere)
    add_para(doc, "Livrabile:", italic=True, space_after=2)
    add_bullets(doc, livrabile)
    if criterii:
        add_para(doc, "Criterii de acceptanță: " + criterii, italic=True)


def build(doc):
    add_heading(doc, "C.4.4. Etapele și fazele de dezvoltare a proiectului", 2)
    add_para(doc,
             "Proiectul va fi realizat în 11 etape majore, fiecare "
             "descompusă în faze cu livrabile și criterii de acceptanță "
             "proprii. Etapele acoperă integral construcția platformei de "
             "la zero: analiză și proiectare, infrastructură, nucleul web "
             "(landing page, înregistrare, autentificare, profiluri, "
             "upload, dashboard), motorul AI, microserviciul LOINC, "
             "raportarea, localizarea multilingvă, modulul B2B, "
             "monetizarea, securizarea și lansarea comercială.", bold=True)

    add_table(doc,
              headers=["Etapa", "Denumire", "Luni", "Pondere efort"],
              rows=[
                  ["E1", "Analiză, specificații și proiectare", "1–3", "8%"],
                  ["E2", "Infrastructură cloud și DevOps", "2–4", "6%"],
                  ["E3", "Nucleul platformei web (landing page, înregistrare, "
                   "autentificare, profiluri, upload, dashboard)", "3–9", "20%"],
                  ["E4", "Motorul AI de extragere și interpretare", "5–11", "16%"],
                  ["E5", "Microserviciul de codificare LOINC", "6–12", "14%"],
                  ["E6", "Raportare și comunicare (PDF, e-mail, FHIR)", "8–12", "7%"],
                  ["E7", "Localizare multilingvă (7 → 30 limbi)", "8–14", "7%"],
                  ["E8", "Modulul B2B — Clinic Access Module", "10–15", "10%"],
                  ["E9", "Monetizare și plăți", "11–15", "5%"],
                  ["E10", "Securitate, conformitate GDPR și testare finală", "12–16", "4%"],
                  ["E11", "Pilotare, marketing și lansare comercială", "14–18", "3%"],
              ],
              col_widths_cm=[1.3, 10.4, 1.9, 2.6])
    add_para(doc,
             "Etapele se suprapun parțial (dezvoltare paralelă pe echipe), "
             "conform diagramei Gantt din capitolul C.6. Detalierea "
             "fazelor fiecărei etape:")

    # ============ ETAPA 1 ============
    add_heading(doc, "Etapa 1 — Analiză, specificații și proiectare (lunile 1–3)", 3)
    add_para(doc,
             "Etapa fundației: transformă viziunea produsului în "
             "specificații executabile. Activități principale: "
             "constituirea echipei și a guvernanței de proiect; interviuri "
             "structurate cu medici, pacienți și manageri de clinici; "
             "elaborarea specificațiilor; selecția tehnologiilor cu "
             "matrice de decizie; proiectarea UX/UI cu testare pe "
             "utilizatori; constituirea corpusului de validare.")
    _faza(doc, "Faza 1.1 — Analiza cerințelor și specificațiile funcționale",
          "Vor fi elaborate specificațiile funcționale complete pentru toate "
          "modulele platformei, pe baza interviurilor cu medici, pacienți și "
          "manageri de clinici (componenta de validare a cererii din C.3.3). "
          "Se vor defini fluxurile utilizator (user journeys) B2C și B2B, "
          "cazurile de utilizare și regulile de business.",
          ["Document de specificații funcționale (SRS) aprobat",
           "Hărți ale fluxurilor utilizator B2C și B2B",
           "Backlog inițial de produs, prioritizat MoSCoW"],
          "SRS acoperă 100% din obiectivele specifice OS3–OS7.")
    _faza(doc, "Faza 1.2 — Proiectarea arhitecturii tehnice",
          "Se va proiecta arhitectura pe straturi (C.4.1), se vor selecta "
          "tehnologiile (matrice de decizie documentată), se va defini "
          "contractul API dintre platforma .NET și microserviciul LOINC și "
          "se vor stabili convențiile de cod, versionare și revizuire.",
          ["Document de arhitectură software (SAD) cu diagrame C4",
           "Contracte API interne (OpenAPI) pentru microserviciul LOINC",
           "Standarde de dezvoltare și politica de code review"],
          "Arhitectura validată prin sesiune de revizuire tehnică formală.")
    _faza(doc, "Faza 1.3 — Proiectarea UX/UI",
          "Vor fi create wireframe-uri și apoi machete de înaltă fidelitate "
          "pentru toate ecranele: landing page, înregistrare, autentificare, "
          "dashboard, încărcare document, raport interpretat, istoric, "
          "portal clinică. Design system propriu (culori, tipografie, "
          "componente) cu accent pe accesibilitate (WCAG 2.1 AA) și pe "
          "încrederea specifică produselor medicale.",
          ["Design system complet", "Machete high-fidelity pentru ≥ 25 ecrane",
           "Prototip interactiv testat pe 20 de utilizatori"],
          "Scor de utilizabilitate SUS ≥ 75 la testarea prototipului.")
    _faza(doc, "Faza 1.4 — Proiectarea modelului de date și a corpusului de validare",
          "Se va proiecta schema bazei de date (C.4.3) și se va constitui "
          "corpusul de validare: 10.000 de buletine de analize reale, "
          "anonimizate, colectate cu acordul partenerilor pilot, care va "
          "servi la măsurarea obiectivă a acurateței motorului AI și a "
          "codificării LOINC pe tot parcursul proiectului.",
          ["Diagrama entitate-relație și scripturile de migrare inițiale",
           "Corpus de validare anonimizat (10.000 documente) cu etichetare "
           "de referință pe un subset de 1.000"],
          "Corpusul acoperă ≥ 30 de laboratoare și ≥ 6 limbi diferite.")

    # ============ ETAPA 2 ============
    add_heading(doc, "Etapa 2 — Infrastructură cloud și DevOps (lunile 2–4)", 3)
    add_para(doc,
             "Etapa care industrializează livrarea: fiecare linie de cod "
             "scrisă ulterior va trece automat prin build, teste, analize "
             "de securitate și deploy controlat. Activități principale: "
             "provizionare IaC a celor trei medii; configurarea "
             "pipeline-urilor pentru ambele servicii; politici de acces cu "
             "privilegiu minim; telemetrie, alertare și gestiunea "
             "costurilor cloud de la prima zi.")
    _faza(doc, "Faza 2.1 — Provizionarea infrastructurii cloud",
          "Se vor crea și configura resursele cloud: găzduire aplicație "
          "web (App Service), bază de date gestionată (Azure SQL), "
          "containere pentru microserviciul LOINC, stocare obiecte pentru "
          "documente, seif de secrete (Key Vault) și telemetrie "
          "(Application Insights). Toate mediile — dezvoltare, staging, "
          "producție — vor fi definite ca infrastructură-sub-formă-de-cod "
          "(IaC), reproductibile și auditabile.",
          ["3 medii complet provizionate prin IaC",
           "Politici de acces pe principiul privilegiului minim"],
          "Un mediu nou poate fi recreat integral din cod în < 1 oră.")
    _faza(doc, "Faza 2.2 — Lanțul CI/CD și calitatea codului",
          "Se va construi pipeline-ul de integrare și livrare continuă: "
          "build automat la fiecare commit, rulare teste unitare și de "
          "integrare, analiză statică de securitate (SAST), scanare "
          "dependențe, deploy automat pe staging și deploy controlat pe "
          "producție (blue-green).",
          ["Pipeline CI/CD funcțional pentru ambele servicii (.NET și Python)",
           "Praguri de calitate: build eșuează sub 70% acoperire teste"],
          "Timp complet de release (commit → producție) < 30 minute.")
    _faza(doc, "Faza 2.3 — Monitorizare, jurnalizare și alertare",
          "Se vor configura tablouri de bord operaționale (disponibilitate, "
          "latență, rată de erori, cost AI per document), jurnalizare "
          "centralizată și alerte automate pentru anomalii.",
          ["Dashboard operațional", "Politici de alertare 24/7",
           "Retenție jurnale: 90 zile operațional, 7 ani audit medical"],
          None)

    # ============ ETAPA 3 ============
    add_heading(doc, "Etapa 3 — Nucleul platformei web (lunile 3–9)", 3)
    add_para(doc,
             "Etapa construiește scheletul complet al produsului vizibil "
             "utilizatorului, în șase faze livrate incremental:")
    _faza(doc, "Faza 3.1 — Landing page publică multilingvă",
          "Va fi dezvoltată pagina publică de prezentare a produsului: "
          "propunerea de valoare, modul de funcționare pas cu pas, "
          "secțiunea de prețuri (freemium/premium/B2B), întrebări "
          "frecvente, pagini legale (termeni, confidențialitate, politica "
          "cookie cu consimțământ GDPR), formular de contact și lista de "
          "așteptare pentru pre-lansare. Pagina va fi optimizată SEO "
          "(date structurate, sitemap, meta multilingv) și va servi drept "
          "instrument de validare a cererii încă din luna 4.",
          ["Landing page publicată în 7 limbi",
           "Mecanism de waitlist cu dublu opt-in",
           "Scor Lighthouse ≥ 90 (performanță, SEO, accesibilitate)"],
          "≥ 3.000 de înscrieri în waitlist până la lansarea MVP.")
    _faza(doc, "Faza 3.2 — Modulul de înregistrare a utilizatorilor",
          "Va fi implementat fluxul complet de creare a contului: formular "
          "de înregistrare cu validare în timp real, verificarea adresei de "
          "e-mail prin link cu expirare, politică de parole puternice, "
          "protecție anti-bot, consimțământ GDPR explicit și granular "
          "(prelucrare date de sănătate — articolul 9 GDPR), și un flux de "
          "onboarding ghidat la prima autentificare (crearea primului "
          "profil de pacient, explicarea creditelor gratuite).",
          ["Flux de înregistrare cu verificare e-mail funcțional",
           "Evidența consimțămintelor cu istoric versionat",
           "Onboarding interactiv la primul login"],
          "Rata de finalizare a înregistrării ≥ 80% în testele de utilizabilitate.")
    _faza(doc, "Faza 3.3 — Modulul de autentificare și gestiunea sesiunilor",
          "Va fi dezvoltat sistemul de autentificare: login cu e-mail și "
          "parolă (hash Argon2id, conform recomandărilor OWASP), opțiunea "
          "„ține-mă minte\u201d (cookie persistent securizat, 30 de zile), "
          "recuperarea parolei prin e-mail cu token cu unică folosință, "
          "limitarea încercărilor eșuate (protecție brute-force), "
          "expirarea și revocarea sesiunilor, precum și fundația pentru "
          "autentificare multi-factor (TOTP) și autentificare socială, "
          "activabile ulterior.",
          ["Autentificare completă cu remember-me și resetare parolă",
           "Protecție brute-force cu blocare progresivă",
           "Jurnalizarea evenimentelor de autentificare în audit trail"],
          "0 vulnerabilități critice la testul de penetrare pe modulul de "
          "autentificare.")
    _faza(doc, "Faza 3.4 — Managementul profilurilor de pacient",
          "Utilizatorul își va putea administra profilurile de pacient "
          "(propriu + membri ai familiei, maximum 5 în tierul premium): "
          "date demografice minime (vârstă, sex — necesare intervalelelor "
          "de referință), fotografie opțională, separarea strictă a "
          "istoricului per profil și mecanisme anti-abuz la crearea "
          "profilurilor.",
          ["CRUD complet profiluri cu limite per tier",
           "Izolarea datelor între profiluri"],
          None)
    _faza(doc, "Faza 3.5 — Modulul de încărcare a documentelor PDF",
          "Va fi construit fluxul de upload: validare client și server "
          "(dimensiune maximă 10 MB, tip MIME, semnătura binară a "
          "fișierului), încărcare pe segmente (chunked) pentru conexiuni "
          "instabile, calcularea amprentei SHA-256 pentru detectarea "
          "duplicatelor (cu opțiunea de reprocesare contra cost), "
          "verificarea soldului de credite și stocarea securizată a "
          "documentului original.",
          ["Upload robust cu validare completă și deduplicare",
           "Gestionarea creditelor la nivel tranzacțional"],
          "Rata de eșec upload < 1% pe conexiuni mobile în testare.")
    _faza(doc, "Faza 3.6 — Dashboard-ul utilizatorului și istoricul",
          "Va fi dezvoltat panoul principal al utilizatorului: lista "
          "interpretărilor cu status vizual per document, filtrare pe "
          "profil și perioadă, vizualizarea detaliată a fiecărui raport, "
          "graficele de evoluție multi-vizită per parametru (pe baza "
          "codurilor LOINC — același analit este recunoscut indiferent de "
          "laboratorul emitent) și gestiunea soldului de credite.",
          ["Dashboard funcțional cu istoric și filtre",
           "Grafice de evoluție temporală per analit (LOINC-based)"],
          "Timp de încărcare dashboard < 2 secunde la 1.000 de înregistrări.")

    # ============ ETAPA 4 ============
    add_heading(doc, "Etapa 4 — Motorul AI de extragere și interpretare (lunile 5–11)", 3)
    add_para(doc,
             "Etapa cu cea mai mare încărcătură de inovație: aici se "
             "construiește capacitatea platformei de a citi orice buletin "
             "și de a-l explica pacientului. Activități principale: "
             "integrarea LLM multimodal cu dublu canal (PDF + text); "
             "promptul ierarhic pe patru niveluri; schema strictă de "
             "răspuns și escaladarea adaptivă; validatorul determinist; "
             "auditorul de completitudine; interpretarea narativă "
             "multilingvă cu validare medicală — totul măsurat continuu "
             "pe corpusul de 10.000 de documente.")
    _faza(doc, "Faza 4.1 — Integrarea modelului LLM multimodal",
          "Va fi implementat serviciul de interpretare (C.4.2-A): apelul "
          "multimodal către modelul AI cu documentul PDF și textul extras "
          "în paralel (dublu canal pentru robustețe), promptul ierarhic pe "
          "patru niveluri și gestionarea limitelor de rată și a erorilor "
          "tranzitorii.",
          ["Serviciu de interpretare funcțional pe corpusul de validare",
           "Jurnalizarea completă a consumului (tokeni, latență, model)"],
          "Extragere reușită pe ≥ 90% din corpus încă din prima iterație.")
    _faza(doc, "Faza 4.2 — Ieșire structurată și reparare JSON",
          "Se va impune schema strictă de răspuns (structured output / "
          "response schema) pentru eliminarea răspunsurilor malformate, "
          "completată cu un reparator tolerant pentru cazurile-limită și "
          "cu escaladarea adaptivă către modelul superior la eșec repetat "
          "(tier promotion).",
          ["Schemă de răspuns versionată", "Mecanism de escaladare configurabil"],
          "Rata de răspunsuri neprocesabile < 0,5%.")
    _faza(doc, "Faza 4.3 — Validatorul clinic determinist",
          "Va fi dezvoltat validatorul descris la C.4.2-C: parsarea a "
          "peste 20 de formate de intervale de referință și recalcularea "
          "deterministă a statusului clinic, cu prevalență asupra "
          "statusului emis de AI.",
          ["Bibliotecă de parsare intervale cu suită de teste dedicată",
           "Raport de discordanțe AI vs. determinist pe corpus"],
          "Acuratețea statusului ≥ 99% pe subsetul etichetat de referință.")
    _faza(doc, "Faza 4.4 — Auditorul de completitudine",
          "Va fi implementat mecanismul de auto-audit (C.4.2-D) cu prag "
          "de 95% și reprocesare automată controlată.",
          ["Auditor configurabil integrat în fluxul de procesare"],
          "Completitudine medie ≥ 95% pe corpusul de validare.")
    _faza(doc, "Faza 4.5 — Interpretarea narativă multilingvă",
          "Va fi dezvoltată generarea explicațiilor în limbaj accesibil: "
          "rezumat general, explicație per analit modificat, semnale de "
          "atenționare fără caracter de diagnostic, recomandarea "
          "consultului medical — în limba selectată de utilizator (7 limbi "
          "la MVP), cu revizuire medicală a șabloanelor de ton și a "
          "delimitărilor etice.",
          ["Interpretare narativă validată medical în 7 limbi",
           "Ghid etic de exprimare aprobat de consultantul medical"],
          "Evaluare orb de către 3 medici: ≥ 90% dintre interpretări "
          "corecte și adecvate.")

    # ============ ETAPA 5 ============
    add_heading(doc, "Etapa 5 — Microserviciul de codificare LOINC (lunile 6–12)", 3)
    add_para(doc,
             "Etapa care construiește diferențiatorul tehnologic central "
             "(C.2.2-1). Activități principale: licențierea și importul "
             "nomenclatorului; pre-calcularea indexului semantic; "
             "dezvoltarea celor patru straturi de potrivire; construirea "
             "și validarea medicală a dicționarului de ancore; calibrarea "
             "iterativă a ponderilor; suita de regresie cu gard de "
             "calitate în CI; integrarea end-to-end cu platforma.")
    _faza(doc, "Faza 5.1 — Corpusul LOINC și indexul semantic",
          "Se va licenția și importa nomenclatorul LOINC (~97.000 "
          "intrări), se vor pre-calcula embeddings-urile semantice pentru "
          "toate denumirile și se va construi indexul de căutare "
          "vectorială cu încărcare la pornirea serviciului.",
          ["Bază LOINC importată și versionată",
           "Index semantic pre-calculat, timp de căutare < 50 ms"],
          None)
    _faza(doc, "Faza 5.2 — Pipeline-ul hibrid de potrivire",
          "Vor fi dezvoltate cele patru straturi (ancore deterministe, "
          "semantic, fuzzy, reguli contextuale) și corecția unit-aware, "
          "cu modelul de ponderare 0,60/0,25/0,15 calibrat iterativ pe "
          "corpusul de validare. Dicționarul de ancore va fi construit și "
          "validat medical pentru analiții care acoperă ≈ 95% din volumul "
          "real de determinări.",
          ["Pipeline complet cu scor de încredere și sursă a deciziei",
           "Dicționar de ancore validat medical (≥ 500 intrări)"],
          "Precizie ≥ 96% pe eșantionul de validare de 10.000 rapoarte.")
    _faza(doc, "Faza 5.3 — API-ul REST și integrarea cu platforma",
          "Microserviciul va expune endpoint-ul de potrivire (POST "
          "/loinc/match), iar platforma .NET va integra clientul rezilient "
          "(C.4.2-E) cu apeluri paralele per analit.",
          ["Contract OpenAPI publicat", "Integrare end-to-end funcțională"],
          "Codificarea completă a unui buletin mediu (25 analiți) < 3 secunde.")
    _faza(doc, "Faza 5.4 — Suita de regresie și calibrarea continuă",
          "Va fi construită o suită automată de regresie pe cazuri reale "
          "adnotate, rulată la fiecare modificare a pipeline-ului, cu "
          "raport de precizie per categorie de analit; ponderile și "
          "regulile se recalibrează doar cu dovada îmbunătățirii globale.",
          ["Suită de regresie automată în CI",
           "Raport de acuratețe per versiune"],
          "Nicio versiune nouă nu scade precizia globală (gard de regresie).")

    # ============ ETAPA 6 ============
    add_heading(doc, "Etapa 6 — Raportare și comunicare (lunile 8–12)", 3)
    _faza(doc, "Faza 6.1 — Generatorul de rapoarte PDF",
          "Va fi dezvoltat raportul PDF final (C.4.2-F): grupare pe "
          "panele, statusuri vizuale, coduri LOINC, metadate inline, "
          "variantă freemium cu mascare și variantă premium completă, "
          "în toate limbile active.",
          ["Șablon PDF profesional multilingv",
           "Generare < 500 ms per raport"],
          None)
    _faza(doc, "Faza 6.2 — Notificări e-mail tranzacționale",
          "Vor fi implementate e-mailurile tranzacționale (verificare "
          "cont, resetare parolă, raport finalizat, sold credite) printr-un "
          "relay SMTP dedicat, cu șabloane localizate și trimitere "
          "asincronă, fără blocarea experienței utilizatorului.",
          ["Set complet de șabloane e-mail în limbile active",
           "Coadă de trimitere asincronă cu reîncercare"],
          None)
    _faza(doc, "Faza 6.3 — Exportul interoperabil HL7 FHIR",
          "Va fi dezvoltat exportul rezultatelor în format HL7 FHIR "
          "(Observation cu coduri LOINC, DiagnosticReport, Patient) — "
          "fundamentul integrărilor B2B și al conformității EHDS.",
          ["Export FHIR validat cu instrumente oficiale de conformitate"],
          "Resursele generate trec 100% validarea de profil FHIR.")

    # ============ ETAPA 7 ============
    add_heading(doc, "Etapa 7 — Localizare multilingvă (lunile 8–14)", 3)
    _faza(doc, "Faza 7.1 — Infrastructura de localizare",
          "Va fi construit sistemul de resurse lingvistice partiționat pe "
          "limbă (interfață, e-mailuri, PDF, mesaje de eroare), cu proces "
          "de traducere gestionat prin platformă dedicată și cu detectarea "
          "automată a limbii preferate.",
          ["Sistem de localizare cu acoperire 100% a șirurilor",
           "7 limbi active la MVP: română, engleză, franceză, germană, "
           "spaniolă, italiană, maghiară"],
          None)
    _faza(doc, "Faza 7.2 — Extinderea la 30 de limbi",
          "Acoperirea lingvistică va fi extinsă progresiv la 30 de limbi "
          "europene și internaționale, cu validare medico-lingvistică "
          "pentru terminologia fiecărei limbi și teste automate de "
          "completitudine a traducerilor.",
          ["30 de limbi active pe toate canalele (UI, interpretare, PDF, "
           "e-mail)", "Matrice de acoperire lingvistică 100%"],
          "Validare medico-lingvistică documentată pentru fiecare limbă.")

    # ============ ETAPA 8 ============
    add_heading(doc, "Etapa 8 — Modulul B2B: Clinic Access Module (lunile 10–15)", 3)
    add_para(doc,
             "Etapa care deschide al doilea motor de venit. Activități "
             "principale: fundația multi-tenant cu izolare verificată; "
             "conturi de organizație cu roluri; procesarea batch asincronă "
             "cu coadă de lucru; dashboard-ul clinicii; API-ul public "
             "documentat și conectorii FHIR pentru integrarea cu sistemele "
             "partenerilor.")
    _faza(doc, "Faza 8.1 — Arhitectura multi-tenant și conturile de organizație",
          "Va fi dezvoltată fundația B2B: entitatea Clinică, utilizatori "
          "cu roluri organizaționale (administrator clinică, operator), "
          "izolarea strictă a datelor per tenant, branding propriu și "
          "subdomeniu dedicat per clinică.",
          ["Multi-tenancy cu izolare verificată prin teste dedicate",
           "Onboarding clinică nouă < 1 zi"],
          None)
    _faza(doc, "Faza 8.2 — Procesarea batch multi-pacient",
          "Clinicile vor putea încărca loturi de buletine (zeci-sute de "
          "documente), procesate asincron cu progres în timp real, "
          "asocierea automată pe pacienți și raport agregat pe lot.",
          ["Procesare batch asincronă cu coadă de lucru",
           "Dashboard de progres în timp real"],
          "Procesarea unui lot de 100 de documente < 30 minute.")
    _faza(doc, "Faza 8.3 — Dashboard-ul clinicii și rapoartele comparative",
          "Va fi dezvoltat panoul clinicii: volume procesate, distribuția "
          "statusurilor, rapoarte comparative multi-pacient și "
          "multi-vizită, export de date și facturare per volum.",
          ["Dashboard B2B complet", "Rapoarte comparative exportabile"],
          None)
    _faza(doc, "Faza 8.4 — Integrări cu sistemele medicale (HIS/LIS)",
          "Va fi expus un API B2B documentat și conectori FHIR pentru "
          "integrarea cu sistemele informatice ale clinicilor și "
          "laboratoarelor partenere.",
          ["API B2B public documentat (OpenAPI)",
           "≥ 2 integrări pilot funcționale cu parteneri"],
          None)

    # ============ ETAPA 9 ============
    add_heading(doc, "Etapa 9 — Monetizare și plăți (lunile 11–15)", 3)
    _faza(doc, "Faza 9.1 — Sistemul de credite și abonamente",
          "Va fi implementat modelul comercial: credite gratuite la "
          "înregistrare (freemium), abonament premium B2C (4,99 EUR/lună), "
          "tiere B2B (199/499/999 EUR/lună), program de recomandare cu "
          "credite bonus și gestionarea tranzacțională a soldurilor.",
          ["Motor de credite și abonamente cu istoric complet",
           "Program de recomandare funcțional"],
          None)
    _faza(doc, "Faza 9.2 — Integrarea gateway-ului de plăți și facturarea",
          "Va fi integrat un procesator de plăți cu suport european "
          "(plăți recurente, SCA/3-D Secure), împreună cu emiterea "
          "automată a facturilor și gestionarea TVA pentru vânzări "
          "transfrontaliere UE (OSS).",
          ["Plăți recurente funcționale în producție",
           "Facturare automată conformă fiscal"],
          "Testare completă a ciclului de viață al abonamentului "
          "(activare, reînnoire, eșec plată, anulare, rambursare).")

    # ============ ETAPA 10 ============
    add_heading(doc, "Etapa 10 — Securitate, conformitate și testare finală (lunile 12–16)", 3)
    _faza(doc, "Faza 10.1 — Audit de securitate și test de penetrare",
          "Un furnizor extern specializat va efectua testarea de "
          "penetrare a întregii platforme (aplicație web, API-uri, "
          "microserviciu, infrastructură), iar constatările vor fi "
          "remediate și retestate.",
          ["Raport de penetrare + dovada remedierii"],
          "0 vulnerabilități critice sau ridicate rămase deschise.")
    _faza(doc, "Faza 10.2 — Conformitate GDPR și evaluarea de impact (DPIA)",
          "Va fi finalizată evaluarea de impact asupra protecției datelor "
          "(obligatorie pentru prelucrarea datelor de sănătate la scară "
          "largă), registrul de prelucrări, procedurile pentru drepturile "
          "persoanelor vizate (acces, portabilitate, ștergere) și "
          "acordurile de prelucrare cu toți sub-procesatorii.",
          ["DPIA aprobată", "Proceduri DSAR operaționale (termen ≤ 30 zile)"],
          None)
    _faza(doc, "Faza 10.3 — Testarea de performanță și fiabilitate",
          "Vor fi executate teste de încărcare (500 procesări simultane), "
          "teste de anduranță și exerciții de recuperare în caz de "
          "dezastru (RPO 1 oră, RTO 4 ore).",
          ["Rapoarte de performanță la țintele NFR",
           "Exercițiu DR documentat și reușit"],
          "Timp de răspuns end-to-end < 15 secunde per raport (percentila 95).")

    # ============ ETAPA 11 ============
    add_heading(doc, "Etapa 11 — Pilotare, marketing și lansare comercială (lunile 14–18)", 3)
    _faza(doc, "Faza 11.1 — Programul pilot",
          "Platforma va fi operată în regim pilot cu 5 clinici partenere "
          "și cu utilizatorii din lista de așteptare (beta privat), cu "
          "colectarea structurată a feedback-ului și iterații rapide.",
          ["5 clinici pilot active", "Beta privat ≥ 1.000 utilizatori",
           "Scrisori de intenție comercială de la partenerii pilot"],
          None)
    _faza(doc, "Faza 11.2 — Lansarea comercială",
          "Lansarea publică B2C și B2B pe piața românească și deschiderea "
          "vânzărilor pe piețele UE acoperite lingvistic, susținută de "
          "campaniile din strategia de marketing (C.7).",
          ["Platformă live în producție cu plăți active",
           "≥ 5.000 utilizatori înregistrați și ≥ 10 contracte B2B în "
           "primele 3 luni de la lansare"],
          "MRR ≥ 8.000 EUR la finalul lunii 18.")
    page_break(doc)

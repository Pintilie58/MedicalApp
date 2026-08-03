from .helpers import add_heading, add_para, add_bullets, add_table, page_break


def build(doc):
    add_heading(doc, "C.4.10. Inventarul ecranelor și al fluxurilor de interfață", 2)
    add_para(doc,
             "Pentru dimensionarea corectă a efortului de dezvoltare și "
             "pentru trasabilitatea completă a livrabilelor de interfață, "
             "au fost inventariate toate ecranele aplicației care vor fi "
             "proiectate și dezvoltate în cadrul etapelor E1 (design) și "
             "E3–E9 (implementare). Inventarul este organizat pe zone "
             "funcționale:")

    add_para(doc, "(A) Zona publică (fără autentificare):", bold=True)
    add_table(doc,
              headers=["Ecran", "Descriere funcțională", "Etapa"],
              rows=[
                  ["Landing page",
                   "Prezentarea propunerii de valoare, modul de funcționare "
                   "pas cu pas, capturi de produs, testimoniale, apeluri la "
                   "acțiune către înregistrare", "F3.1"],
                  ["Pagina de prețuri",
                   "Compararea planurilor freemium/premium și a tierelor "
                   "B2B, întrebări frecvente despre facturare", "F3.1"],
                  ["Pagina B2B pentru clinici",
                   "Beneficii dedicate clinicilor, calculator de economie "
                   "de timp, formular de solicitare demo", "F3.1"],
                  ["Întrebări frecvente (FAQ)",
                   "Răspunsuri structurate pe categorii: produs, date "
                   "personale, plăți, limitări medicale", "F3.1"],
                  ["Pagini legale",
                   "Termeni și condiții, politica de confidențialitate, "
                   "politica cookie cu gestionarea consimțământului", "F3.1"],
                  ["Formular de contact",
                   "Mesaje către echipă, cu protecție anti-spam", "F3.1"],
                  ["Lista de așteptare (waitlist)",
                   "Înscriere pre-lansare cu dublu opt-in și contor social", "F3.1"],
              ],
              col_widths_cm=[3.8, 10.4, 1.6])

    add_para(doc, "(B) Zona de cont și autentificare:", bold=True)
    add_table(doc,
              headers=["Ecran", "Descriere funcțională", "Etapa"],
              rows=[
                  ["Înregistrare",
                   "Formular cu validare în timp real, indicator de putere "
                   "a parolei, consimțăminte GDPR granulare", "F3.2"],
                  ["Verificare e-mail",
                   "Confirmarea adresei prin link cu expirare; retrimitere "
                   "controlată", "F3.2"],
                  ["Onboarding ghidat",
                   "Pași interactivi la primul login: crearea primului "
                   "profil, explicarea creditelor gratuite, primul upload", "F3.2"],
                  ["Autentificare",
                   "Login cu e-mail și parolă, opțiunea „ține-mă minte\u201d "
                   "(30 zile), mesaje de eroare sigure", "F3.3"],
                  ["Recuperare parolă",
                   "Solicitare + setare parolă nouă prin token cu unică "
                   "folosință", "F3.3"],
                  ["Setările contului",
                   "Date personale, limba preferată, schimbarea parolei, "
                   "gestiunea consimțămintelor, ștergerea contului (GDPR)", "F3.3"],
              ],
              col_widths_cm=[3.8, 10.4, 1.6])

    add_para(doc, "(C) Zona B2C (utilizator autentificat):", bold=True)
    add_table(doc,
              headers=["Ecran", "Descriere funcțională", "Etapa"],
              rows=[
                  ["Dashboard principal",
                   "Sinteza ultimelor interpretări, sold credite, acces "
                   "rapid la upload și profiluri", "F3.6"],
                  ["Gestiunea profilurilor",
                   "Lista profilurilor de familie, adăugare/editare/ștergere "
                   "cu limite per tier", "F3.4"],
                  ["Încărcare document",
                   "Zonă drag-and-drop, progres pe segmente, validări, "
                   "detectarea duplicatelor", "F3.5"],
                  ["Procesare în curs",
                   "Stări în timp real: extragere → validare → codificare → "
                   "raport", "F3.5"],
                  ["Raport interpretat",
                   "Rezultatele grupate pe panele, statusuri vizuale, "
                   "explicații per analit, cod LOINC, descărcare PDF", "F3.6"],
                  ["Istoric interpretări",
                   "Filtrare pe profil/perioadă/status, căutare, acces la "
                   "rapoartele anterioare", "F3.6"],
                  ["Evoluție grafică",
                   "Grafic temporal per parametru (LOINC-based), comparație "
                   "între vizite și laboratoare diferite", "F3.6"],
                  ["Abonament și credite",
                   "Upgrade la premium, istoric plăți, facturi, cod de "
                   "recomandare", "F9.1"],
              ],
              col_widths_cm=[3.8, 10.4, 1.6])

    add_para(doc, "(D) Zona B2B (Clinic Access Module):", bold=True)
    add_table(doc,
              headers=["Ecran", "Descriere funcțională", "Etapa"],
              rows=[
                  ["Dashboard clinică",
                   "Volume procesate, distribuția statusurilor, consum și "
                   "facturare curentă", "F8.3"],
                  ["Încărcare batch",
                   "Upload multiplu (zeci–sute de documente), asociere pe "
                   "pacienți, progres per lot", "F8.2"],
                  ["Rezultate lot",
                   "Tabel agregat cu toți pacienții lotului, semnalarea "
                   "valorilor patologice, export", "F8.2"],
                  ["Raport comparativ",
                   "Comparații multi-pacient și multi-vizită pe parametri "
                   "selectați", "F8.3"],
                  ["Administrare organizație",
                   "Utilizatori și roluri, branding propriu, subdomeniu, "
                   "setări de facturare", "F8.1"],
                  ["Integrare API",
                   "Chei API, documentație interactivă, jurnalul apelurilor", "F8.4"],
              ],
              col_widths_cm=[3.8, 10.4, 1.6])

    add_para(doc, "(E) Zona de administrare a platformei (back-office):", bold=True)
    add_table(doc,
              headers=["Ecran", "Descriere funcțională", "Etapa"],
              rows=[
                  ["Panou operațional",
                   "Indicatori de sănătate a platformei: procesări, erori, "
                   "cost AI per document, latențe", "E4/E10"],
                  ["Gestiunea utilizatorilor",
                   "Căutare, suport, ajustare credite, blocare conturi "
                   "abuzive, jurnal de audit", "E3"],
                  ["Gestiunea clinicilor",
                   "Onboarding tenant nou, configurare tarife, monitorizare "
                   "SLA", "E8"],
                  ["Calitatea codificării",
                   "Rapoarte de acuratețe LOINC per versiune, cazuri cu "
                   "scor scăzut pentru revizuire medicală", "F5.4"],
              ],
              col_widths_cm=[3.8, 10.4, 1.6])
    add_para(doc,
             "Total: peste 30 de ecrane distincte, fiecare cu variante "
             "responsive (desktop/tabletă/mobil) și localizate în toate "
             "limbile active — dimensiune care fundamentează efortul "
             "alocat etapelor de design (F1.3) și dezvoltare frontend.",
             italic=True)
    page_break(doc)

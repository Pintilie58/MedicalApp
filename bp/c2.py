from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.2. Caracterul inovativ al aplicației", 1)
    add_para(doc,
             "Caracterul inovativ al proiectului MyMedicalApp este de tip "
             "inovare de produs (produs software nou pe piața românească și "
             "europeană) combinată cu inovare de proces (pipeline hibrid "
             "AI + algoritmic de codificare medicală, inexistent în soluțiile "
             "comerciale actuale). Prezentul capitol detaliază stadiul "
             "tehnologiei, elementele concrete de noutate și componenta de "
             "Inteligență Artificială.", bold=True)

    # ---------------- C.2.1 ----------------
    add_heading(doc, "C.2.1. Stadiul actual al tehnologiei (state of the art)", 2)
    add_para(doc,
             "Analiza pieței și a literaturii de specialitate relevă trei "
             "categorii de soluții existente, fiecare cu limitări structurale:")
    add_numbered(doc, [
        "Aplicații B2C de „explicare a analizelor\u201d (ex. aplicații mobile "
        "internaționale de tip lab-result checker): impun introducerea "
        "manuală a valorilor de către utilizator, nu citesc PDF-uri "
        "eterogene, nu codifică LOINC, acoperă 1–2 limbi și un număr "
        "restrâns de analiți uzuali;",
        "Platforme enterprise de interoperabilitate (motoare de terminologie "
        "medicală, servicii de mapare LOINC pentru spitale): adresează "
        "exclusiv segmentul enterprise, cu costuri de licențiere de peste "
        "100.000 EUR/an, implementări de 6–18 luni și fără nicio componentă "
        "orientată către pacient;",
        "Funcționalități generice ale asistenților AI de larg consum: pot "
        "rezuma un document medical, dar nu oferă garanții de completitudine, "
        "nu efectuează validare deterministă a statusurilor clinice, nu "
        "codifică standardizat, nu persistă istoricul medical și nu sunt "
        "conforme cu cerințele GDPR/AI Act pentru prelucrarea datelor de "
        "sănătate."
    ])
    add_para(doc,
             "Concluzia analizei: nu există în prezent, la nivel european, un "
             "produs comercial care să integreze într-un singur flux "
             "extragerea automată din PDF-uri de laborator eterogene, "
             "codificarea LOINC automată de precizie ridicată, validarea "
             "clinică deterministă și interpretarea narativă multilingvă "
             "adresată pacientului. MyMedicalApp va fi construit exact pe "
             "această poziție liberă a pieței.")

    # ---------------- C.2.2 ----------------
    add_heading(doc, "C.2.2. Elementele de noutate ale proiectului", 2)
    add_para(doc,
             "Proiectul va dezvolta de la zero următoarele elemente cu "
             "caracter inovativ, care împreună formează un produs fără "
             "echivalent comercial direct:")

    add_para(doc, "(1) Pipeline hibrid de codificare LOINC în patru straturi "
             "(inovația centrală a proiectului):", bold=True)
    add_para(doc,
             "Codificarea LOINC automată este o problemă dificilă: "
             "nomenclatorul conține circa 97.000 de coduri, iar denumirile "
             "analizelor din buletinele reale variază enorm (limbă, "
             "abrevieri, sinonime, metode de laborator). Proiectul va "
             "construi un microserviciu dedicat care combină, printr-un "
             "model matematic de ponderare original, patru tehnici "
             "complementare:")
    add_numbered(doc, [
        "Strat determinist de ancore: dicționar curat de termeni canonici "
        "mapați direct la coduri LOINC validate medical, cu potrivire "
        "exactă după normalizare — garantează corectitudinea pe analiții "
        "frecvenți (top 500 acoperă ≈ 95% din volumul real);",
        "Strat semantic vectorial: fiecare denumire de analiză este "
        "transformată într-un vector numeric (embedding de 384 dimensiuni, "
        "model SentenceTransformer) și comparată prin similaritate cosinus "
        "cu embeddings pre-calculate ale tuturor celor ~97.000 de denumiri "
        "LOINC — captează sinonimia și variațiile de exprimare;",
        "Strat de potrivire fuzzy multi-sursă: distanțe de editare "
        "token-based (RapidFuzz) calculate încrucișat între denumirea "
        "normalizată, denumirea brută din PDF și câmpurile LOINC — "
        "robustețe la greșeli de tipar și abrevieri;",
        "Strat de reguli contextuale: extragerea specimenului (ser, urină, "
        "sânge integral), a metodei de laborator și a unității de măsură "
        "din contextul brut al documentului (antetul de panel și linia "
        "analitului), cu corecție finală „unit-aware\u201d care alege între "
        "variantele masice și molare ale aceluiași analit în funcție de "
        "unitatea raportată."
    ])
    add_para(doc,
             "Scorul final se calculează printr-o combinație ponderată "
             "calibrată experimental (ponderea semantică 0,60 · fuzzy 0,25 · "
             "reguli 0,15), cu prag de încredere sub care codul este respins "
             "pentru a nu introduce erori. Această arhitectură în straturi, "
             "cu context documentar ierarhic transmis din PDF până în "
             "algoritmul de decizie, constituie element de noutate față de "
             "orice soluție comercială identificată.", italic=True)

    add_para(doc, "(2) Extragere ierarhică a contextului documentar din PDF:", bold=True)
    add_para(doc,
             "Motorul AI nu va extrage doar perechi „denumire–valoare\u201d, ci "
             "și contextul ierarhic complet al fiecărui analit: antetul de "
             "panel sub care apare (ex. „Hemoleucogramă completă | Formulă "
             "leucocitară\u201d) și linia brută integrală a analitului. Acest "
             "context este propagat în tot lanțul de procesare — de la "
             "codificarea LOINC (unde dezambiguizează metoda și specimenul) "
             "până la raportul PDF final (unde reconstruiește vizual "
             "structura originală a buletinului). Nicio soluție analizată nu "
             "conservă și nu exploatează acest context ierarhic.")

    add_para(doc, "(3) Escaladare adaptivă între modele AI (tier promotion):", bold=True)
    add_para(doc,
             "Sistemul va folosi implicit un model AI rapid și economic, cu "
             "escaladare automată către un model superior exclusiv în "
             "cazurile în care răspunsul nu trece validările de structură și "
             "completitudine. Mecanismul optimizează simultan costul "
             "(≈ 90% din volum procesat pe modelul economic) și calitatea "
             "(cazurile dificile primesc automat capacitate de calcul "
             "superioară) — un compromis inginerește inovator față de "
             "abordarea „un singur model\u201d a competitorilor.")

    add_para(doc, "(4) Validare clinică deterministă post-AI:", bold=True)
    add_para(doc,
             "Toate statusurile clinice (normal / crescut / scăzut / "
             "borderline) emise de modelul AI vor fi recalculate determinist "
             "de un validator algoritmic propriu, capabil să interpreteze "
             "peste 20 de formate de intervale de referință (intervale "
             "numerice, praguri, intervale diferențiate pe sex, rezultate "
             "calitative). Acest strat de siguranță — AI-ul propune, "
             "algoritmul verifică — răspunde direct cerințelor AI Act "
             "privind supravegherea sistemelor AI în sănătate și constituie "
             "un diferențiator de încredere față de soluțiile pur generative.")

    add_para(doc, "(5) Auditor de completitudine a extragerii:", bold=True)
    add_para(doc,
             "Un mecanism original de auto-audit va compara numărul de "
             "analize declarate de modelul AI ca fiind prezente în document "
             "cu numărul efectiv extras, declanșând automat reprocesarea "
             "atunci când completitudinea scade sub 95%. Se elimină astfel "
             "riscul principal al extragerii generative: omisiunile silențioase.")

    add_para(doc, "(6) Interpretare narativă multilingvă nativă:", bold=True)
    add_para(doc,
             "Interpretarea rezultatelor va fi generată direct în limba "
             "pacientului (7 limbi la lansarea MVP, 30 de limbi la finalul "
             "proiectului), cu ton empatic, fără diagnostice, cu delimitări "
             "etice stricte și recomandarea consultului medical. Localizarea "
             "completă (interfață + interpretare + raport PDF + e-mailuri) "
             "la această scară lingvistică nu există în prezent pe piață.")

    add_para(doc, "(7) Arhitectură duală cloud / on-premise:", bold=True)
    add_para(doc,
             "Platforma va fi proiectată de la început pentru două moduri de "
             "livrare: SaaS multi-tenant în cloud (B2C și B2B standard) și "
             "instalare on-premise containerizată pentru clienți enterprise "
             "cu cerințe stricte de suveranitate a datelor (spitale, rețele "
             "de laboratoare). Aceeași bază de cod, două modele de business.")

    # ---------------- C.2.3 ----------------
    add_heading(doc, "C.2.3. Specificarea componentelor de Inteligență Artificială", 2)
    add_para(doc,
             "Conform cerințelor ghidului privind evidențierea componentei "
             "AI, proiectul integrează următoarele tehnologii de Inteligență "
             "Artificială:")
    add_table(doc,
              headers=["Componentă AI", "Tehnologie", "Rol în platformă"],
              rows=[
                  ["Extragere multimodală din PDF",
                   "LLM multimodal (familia Google Gemini), prompt "
                   "engineering ierarhic pe 4 niveluri",
                   "Citește PDF-ul (vizual + text), extrage structurat "
                   "analiții, valorile, unitățile, intervalele și contextul "
                   "ierarhic"],
                  ["Interpretare narativă",
                   "LLM generativ cu ghidaj etic strict (fără diagnostice)",
                   "Generează explicații în limbaj accesibil, în limba "
                   "pacientului, per analit și per ansamblu"],
                  ["Codificare semantică LOINC",
                   "SentenceTransformer all-MiniLM-L6-v2, embeddings 384D, "
                   "similaritate cosinus pe ~97.000 intrări",
                   "Găsește candidații LOINC semantici pentru denumiri "
                   "nevăzute anterior"],
                  ["Potrivire fuzzy",
                   "RapidFuzz token_set_ratio multi-sursă",
                   "Robustețe la abrevieri, greșeli de tipar, variații "
                   "lexicale"],
                  ["Escaladare adaptivă",
                   "Orchestrare algoritmică multi-model (economic → "
                   "performant)",
                   "Optimizare cost/calitate per document"],
                  ["Validare deterministă",
                   "Algoritmi simbolici (non-AI) de parsare a intervalelor",
                   "Plasă de siguranță umano-verificabilă peste ieșirile AI "
                   "(cerință AI Act)"],
              ],
              col_widths_cm=[4.2, 5.6, 6.4])
    add_para(doc,
             "Poziționare față de Regulamentul (UE) 2024/1689 (AI Act): "
             "platforma nu emite diagnostice și nu ia decizii medicale "
             "autonome; oferă informare și structurare de date, cu "
             "recomandarea explicită și sistematică a consultului medical. "
             "Prin validarea deterministă, jurnalizarea completă a apelurilor "
             "AI și transparența față de utilizator, S.C. FIXMEDICAL S.R.L. "
             "se poziționează proactiv în zona de conformitate, anticipând "
             "cerințele aplicabile sistemelor AI din domeniul sănătății.")

    # ---------------- C.2.4 ----------------
    add_heading(doc, "C.2.4. Comparație cu soluțiile existente și diferențiere", 2)
    add_table(doc,
              headers=["Criteriu", "MyMedicalApp (proiect)",
                       "Aplicații B2C existente", "Platforme enterprise"],
              rows=[
                  ["Citire PDF eterogen", "Da — AI multimodal",
                   "Nu — introducere manuală", "Parțial — doar formate HL7"],
                  ["Codificare LOINC automată", "Da — pipeline hibrid ≥ 96%",
                   "Nu", "Da — dar manual-asistată"],
                  ["Interpretare pentru pacient", "Da — 30 limbi",
                   "Parțial — 1-2 limbi", "Nu"],
                  ["Validare clinică deterministă", "Da", "Nu", "N/A"],
                  ["Istoric și evoluție grafică", "Da — multi-vizită",
                   "Parțial", "Da"],
                  ["Model B2B batch + FHIR", "Da", "Nu", "Da"],
                  ["Preț B2C", "0 EUR freemium / 4,99 EUR premium",
                   "0–2 EUR", "Nu se aplică"],
                  ["Preț B2B", "de la 199 EUR/lună",
                   "Nu se aplică", "≥ 100.000 EUR/an"],
                  ["Timp de implementare client B2B", "< 1 zi (SaaS)",
                   "N/A", "6–18 luni"],
              ],
              col_widths_cm=[4.4, 4.4, 3.7, 3.7])
    add_para(doc,
             "Diferențierea strategică rezultă din combinarea, în același "
             "produs, a trei capabilități pe care competitorii le oferă cel "
             "mult izolat: acuratețe enterprise la codificare, experiență "
             "B2C accesibilă și multilingvism la scară europeană.")

    # ---------------- C.2.5 ----------------
    add_heading(doc, "C.2.5. Riscul tehnologic și industrial asociat inovării", 2)
    add_para(doc,
             "Fiind un proiect greenfield cu grad ridicat de noutate, riscul "
             "tehnologic principal este atingerea țintelor de acuratețe "
             "(≥ 95% completitudine, ≥ 96% precizie LOINC) pe diversitatea "
             "reală de formate de buletine. Strategia de atenuare este "
             "încorporată în chiar arhitectura propusă: straturile "
             "deterministe (ancore, validator, auditor) plafonează eroarea "
             "componentelor generative, iar calibrarea ponderilor se face "
             "iterativ pe un corpus de validare de 10.000 de rapoarte reale "
             "anonimizate, constituit în primele luni ale proiectului. "
             "Detalierea completă a riscurilor se regăsește în capitolul C.9.")

    # ---------------- C.2.6 ----------------
    add_heading(doc, "C.2.6. Strategia de proprietate intelectuală", 2)
    add_para(doc,
             "Protejarea rezultatelor inovative ale proiectului se va "
             "realiza pe mai multe niveluri complementare:")
    add_bullets(doc, [
        "Drept de autor asupra întregului cod sursă, deținut integral de "
        "S.C. FIXMEDICAL S.R.L. (clauze de cesiune în toate contractele "
        "de muncă și de subcontractare);",
        "Secret comercial (know-how): modelul de ponderare al "
        "pipeline-ului LOINC, dicționarul de ancore validat medical și "
        "prompturile ierarhice — active necomunicate public, protejate "
        "prin acorduri de confidențialitate și acces restricționat;",
        "Marcă înregistrată: denumirea și identitatea vizuală "
        "MyMedicalApp vor fi înregistrate la EUIPO (marcă UE) în primul "
        "an de proiect;",
        "Analiză de brevetabilitate: în luna 12 se va evalua, cu "
        "consultanță specializată, oportunitatea protejării prin brevet a "
        "metodei hibride de codificare cu context documentar ierarhic;",
        "Conformitate cu licențele terților: nomenclatorul LOINC "
        "(licență Regenstrief), bibliotecile open-source (audit de "
        "licențe în CI) și termenii API-urilor comerciale."
    ])
    page_break(doc)

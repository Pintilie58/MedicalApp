from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.8. Impactul proiectului", 1)

    # ---------------- C.8.1 ----------------
    add_heading(doc, "C.8.1. Rezultate cantitative așteptate", 2)
    add_table(doc,
              headers=["Indicator", "La finalul proiectului (luna 18)",
                       "Anul 3 post-implementare"],
              rows=[
                  ["Utilizatori B2C înregistrați", "≥ 5.000", "≥ 150.000"],
                  ["Clienți B2B activi", "≥ 10", "≥ 120"],
                  ["Documente procesate / an", "≥ 30.000", "≥ 2.000.000"],
                  ["Determinări codificate LOINC / an", "≥ 700.000",
                   "≥ 10.000.000"],
                  ["Limbi acoperite", "30", "30+"],
                  ["Venit recurent lunar (MRR)", "≥ 8.000 EUR", "≥ 150.000 EUR"],
                  ["Cifră de afaceri anuală", "—", "1.800.000 EUR"],
                  ["Cifră de afaceri cumulată (3 ani)", "—", "≥ 3.500.000 EUR"],
                  ["Locuri de muncă înalt calificate", "10", "≥ 18"],
              ],
              col_widths_cm=[6.4, 4.9, 4.9])

    # ---------------- C.8.2 ----------------
    add_heading(doc, "C.8.2. Rezultate calitative și impact social", 2)
    add_bullets(doc, [
        "Creșterea alfabetizării în sănătate: pacienții înțeleg efectiv "
        "rezultatele investigațiilor proprii, în limba maternă — impact "
        "direct asupra unei probleme care afectează peste jumătate din "
        "populația României;",
        "Prevenție îmbunătățită: valorile patologice sunt semnalate clar "
        "și prompt, reducând întârzierile de prezentare la medic;",
        "Reducerea anxietății medicale nejustificate prin explicații "
        "calibrate și non-alarmiste;",
        "Incluziune: accesibilitate WCAG 2.1 AA și acoperirea a 30 de "
        "limbi deservesc vârstnici, minorități lingvistice și diaspora;",
        "Timp medical recuperat: clinicile reduc timpul consumat cu "
        "explicarea rezultatelor de rutină, realocându-l actului medical;",
        "Contribuție la interoperabilitate: fiecare document procesat "
        "devine set de date structurate, codificate internațional, "
        "pregătite pentru dosarul electronic de sănătate și EHDS."
    ])

    # ---------------- C.8.3 ----------------
    add_heading(doc, "C.8.3. Efectul multiplicator și replicabilitatea", 2)
    add_para(doc,
             "Tehnologia dezvoltată — extragere AI din documente medicale "
             "eterogene + codificare terminologică hibridă — este "
             "replicabilă pe verticale adiacente: imagistică (rapoarte "
             "radiologice), externări (scrisori medicale), medicina muncii "
             "și studiile clinice (curățarea datelor de laborator). "
             "Fiecare verticală nouă reutilizează nucleul construit în "
             "proiect, multiplicând valoarea investiției inițiale. "
             "Suplimentar, expunerea API-ului B2B permite ecosistemului "
             "local de e-health (telemedicină, aplicații de wellness, "
             "asigurători) să construiască peste platformă, cu efect de "
             "antrenare asupra întregului sector.")

    # ---------------- C.8.4 ----------------
    add_heading(doc, "C.8.4. Sustenabilitatea financiară post-implementare", 2)
    add_para(doc,
             "Modelul de venit recurent (abonamente B2C + B2B) asigură "
             "sustenabilitatea operațională după încheierea finanțării: "
             "pragul de rentabilitate lunar (≈ 21.000 EUR MRR) este "
             "estimat a fi atins în anul 2 post-lansare, iar marja brută "
             "a modelului SaaS (peste 75% după costurile AI per document) "
             "finanțează dezvoltarea continuă. Fluxurile detaliate de "
             "numerar și proiecțiile pe 5 ani sunt prezentate în Partea a "
             "III-a a planului de afaceri.")

    # ---------------- C.8.5 ----------------
    add_heading(doc, "C.8.5. Sustenabilitate de mediu", 2)
    add_bullets(doc, [
        "Produs 100% digital: reduce tipărirea și transportul fizic al "
        "documentelor medicale;",
        "Infrastructură cloud operată în centre de date cu angajamente de "
        "energie regenerabilă și optimizare automată a resurselor "
        "(scale-to-zero pe medii non-productive);",
        "Escaladarea adaptivă între modele AI minimizează consumul de "
        "calcul per document (≈ 90% din volum pe modelul economic);",
        "Politică internă paperless și lucru hibrid pentru echipă."
    ])

    # ---------------- C.8.6 ----------------
    add_heading(doc, "C.8.6. Contribuția la strategiile naționale și europene", 2)
    add_para(doc,
             "Prin natura sa, proiectul livrează simultan pe trei axe "
             "strategice: digitalizare (produs SaaS AI dezvoltat de o IMM "
             "românească, aliniat POCIDIF), sănătate (alfabetizare, "
             "prevenție, interoperabilitate EHDS) și competitivitate "
             "(export de software cu valoare adăugată mare pe piața unică, "
             "creare de locuri de muncă înalt calificate în România). "
             "Indicatorii de program relevanți — întreprinderi sprijinite "
             "pentru introducerea AI, produse digitale noi lansate, locuri "
             "de muncă create — sunt îndepliniți integral de rezultatele "
             "enumerate la C.8.1.")
    page_break(doc)

from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.5. Personal și instruire", 1)

    # ---------------- C.5.1 ----------------
    add_heading(doc, "C.5.1. Structura echipei de proiect", 2)
    add_para(doc,
             "Pentru realizarea proiectului, S.C. FIXMEDICAL S.R.L. va "
             "constitui o echipă dedicată de 10 persoane, combinând "
             "angajări noi cu expertiză externă punctuală. Structura, "
             "alocarea și costurile estimate:")
    add_table(doc,
              headers=["Rol", "Nr.", "Alocare", "Cost salarial anual estimat (EUR brut)"],
              rows=[
                  ["Manager de proiect / Product Owner", "1", "100%", "48.000"],
                  ["Arhitect software (.NET)", "1", "100%", "60.000"],
                  ["Dezvoltator senior backend (.NET)", "2", "100%", "96.000"],
                  ["Dezvoltator AI/ML (Python)", "1", "100%", "54.000"],
                  ["Dezvoltator frontend", "1", "100%", "42.000"],
                  ["Inginer DevOps / securitate", "1", "100%", "48.000"],
                  ["Inginer QA / testare automată", "1", "100%", "36.000"],
                  ["Consultant medical (medic laborator)", "1", "50%", "24.000"],
                  ["Specialist marketing & vânzări B2B", "1", "100%", "32.000"],
              ],
              col_widths_cm=[7.2, 1.2, 2.0, 5.8])
    add_para(doc,
             "Total cost salarial anual estimat: 340.000 EUR brut, "
             "echivalentul a aproximativ 168 luni-om de efort pe durata "
             "celor 18 luni de implementare.", italic=True)

    # ---------------- C.5.2 ----------------
    add_heading(doc, "C.5.2. Roluri și responsabilități", 2)
    add_bullets(doc, [
        "Managerul de proiect răspunde de planificare, bugetare, "
        "raportarea către finanțator, gestiunea riscurilor și "
        "prioritizarea backlog-ului de produs;",
        "Arhitectul software definește și menține arhitectura (C.4.1), "
        "contractele API, standardele de cod și conduce revizuirile "
        "tehnice;",
        "Dezvoltatorii backend construiesc nucleul platformei (etapele "
        "E3, E4, E6, E8, E9): autentificare, upload, orchestrarea AI, "
        "raportare, modul B2B, plăți;",
        "Dezvoltatorul AI/ML construiește microserviciul LOINC (E5): "
        "indexul semantic, pipeline-ul hibrid, suita de regresie și "
        "calibrarea acurateței;",
        "Dezvoltatorul frontend implementează design system-ul, landing "
        "page-ul, dashboard-urile și accesibilitatea WCAG;",
        "Inginerul DevOps răspunde de infrastructura IaC, CI/CD, "
        "monitorizare, backup/DR și de implementarea controalelor de "
        "securitate;",
        "Inginerul QA construiește piramida de teste, scenariile "
        "end-to-end și gardurile de regresie a acurateței;",
        "Consultantul medical validează dicționarul de ancore LOINC, "
        "șabloanele de interpretare, delimitările etice și corectitudinea "
        "medicală a livrabilelor;",
        "Specialistul de marketing execută strategia C.7: validarea "
        "cererii, waitlist, campanii, pilotare și vânzare B2B."
    ])
    add_para(doc,
             "Matricea RACI detaliată per etapă (E1–E11) va fi anexată "
             "planului de management al proiectului la contractare; "
             "principiul general: Product Owner-ul este Accountable pentru "
             "livrabilele de produs, arhitectul pentru cele tehnice, iar "
             "consultantul medical are drept de veto pe conținutul medical.")

    # ---------------- C.5.3 ----------------
    add_heading(doc, "C.5.3. Planul de instruire a personalului", 2)
    add_para(doc,
             "Deoarece proiectul introduce tehnologii de vârf (LLM-uri "
             "multimodale, embeddings semantice, interoperabilitate FHIR), "
             "planul include un program structurat de formare cu buget "
             "total de 22.000 EUR:")
    add_table(doc,
              headers=["Tematică", "Participanți", "Durată", "Buget (EUR)"],
              rows=[
                  ["Inginerie prompt și integrare LLM în producție",
                   "3 dezvoltatori", "5 zile", "4.500"],
                  ["MLOps: embeddings, căutare vectorială, evaluarea "
                   "acurateței", "2 persoane", "5 zile", "3.500"],
                  ["Interoperabilitate HL7 FHIR și terminologii medicale "
                   "(LOINC)", "3 persoane", "4 zile", "4.000"],
                  ["Securitate aplicativă (OWASP) și pregătire ISO 27001",
                   "2 persoane", "4 zile", "3.500"],
                  ["GDPR pentru date de sănătate și DPIA", "3 persoane",
                   "2 zile", "2.000"],
                  ["Certificare cloud (administrare și optimizare costuri)",
                   "2 persoane", "5 zile", "3.000"],
                  ["Vânzare consultativă B2B în healthcare", "2 persoane",
                   "3 zile", "1.500"],
              ],
              col_widths_cm=[7.6, 3.0, 2.0, 2.4])

    # ---------------- C.5.4 ----------------
    add_heading(doc, "C.5.4. Expertiză externă subcontractată", 2)
    add_bullets(doc, [
        "Audit extern de securitate și test de penetrare (etapa E10) — "
        "furnizor certificat, estimat 15.000 EUR;",
        "Servicii juridice specializate GDPR/e-health pentru DPIA și "
        "acorduri de prelucrare — estimat 8.000 EUR;",
        "Servicii de traducere și validare medico-lingvistică pentru "
        "extinderea la 30 de limbi — estimat 25.000 EUR;",
        "Design grafic pentru identitatea vizuală și materialele de "
        "marketing — estimat 6.000 EUR."
    ])
    page_break(doc)

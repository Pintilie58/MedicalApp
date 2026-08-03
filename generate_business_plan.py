"""
Generator Plan de Afaceri — PARTEA A II-A: DESCRIEREA PROIECTULUI
MyMedicalApp / S.C. FIXMEDICAL S.R.L. — POCIDIF 2021-2027, conform Anexei 4.
Abordare greenfield: proiect dezvoltat integral de la zero.
Output: /app/Plan_Afaceri_MyMedicalApp_FIXMEDICAL.docx
"""
from bp.helpers import make_doc
from bp import cover, c1, c2, c3, c4_arhitectura, c4_etape, c4_specificatii
from bp import c4_ecrane, c4_dictionar, c5, c6, c7, c8, c9, glosar

doc = make_doc()

cover.build(doc)
c1.build(doc)
c2.build(doc)
c3.build(doc)
c4_arhitectura.build(doc)
c4_etape.build(doc)
c4_specificatii.build(doc)
c4_ecrane.build(doc)
c4_dictionar.build(doc)
c5.build(doc)
c6.build(doc)
c7.build(doc)
c8.build(doc)
c9.build(doc)
glosar.build(doc)

OUT = "/app/Plan_Afaceri_MyMedicalApp_FIXMEDICAL.docx"
doc.save(OUT)
print(f"Document generat: {OUT}")

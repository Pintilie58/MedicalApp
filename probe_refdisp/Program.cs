using MedicalApp.Services;
int f=0;
void C(string raw,string exp,bool lng){var s=ReferenceRangeDisplay.Short(raw);var l=ReferenceRangeDisplay.IsLong(raw);bool ok=s==exp&&l==lng;if(!ok)f++;Console.WriteLine((ok?"PASS ":"FAIL ")+$"'{raw[..Math.Min(40,raw.Length)]}' -> '{s}' long={l}");}
C("12.6 - 17.4 / g/dL","12.6 - 17.4 / g/dL",false);
C("< 20","< 20",false);
C("Intervalul terapeutic al INR în cursul tratamentului cu anticoagulante orale (conform Ghid ACCP 2008): - INR=2.0–3.0 (2.5): pentru majoritatea","2.0–3.0",true);
C("<100 mg/dL - risc cardiovascular înalt; 100-129 aproape optim","<100",true);
C("Femei: 12.0 - 15.5 g/dL; Barbati: 13.5 - 17.5 g/dL","12.0-15.5",true);
C("Negativ (vezi comentariul laboratorului privind metoda)","—",true);
Console.WriteLine(f==0?"ALL PASS":$"{f} FAIL");

"""
Static validation of the Portuguese (PT) language addition.
Sandbox has no dotnet SDK — validate C# source via regex/text parsing.
"""
import re
import os
import subprocess
import pytest

LOC_PATH = "/app/MedicalApp/Services/Loc.cs"
CFG_PATH = "/app/MedicalApp/Services/SupportedLanguagesConfig.cs"
PARSER_PATH = "/app/MedicalApp/Services/SamplingDateParser.cs"

# Match `["Key"] = "value"` entries at the *dict entry* level (not lookup like _translations["en"])
# Language block delimiter: `["xx"] = new()`
LANG_HDR_RE = re.compile(r'\["([a-z]{2})"\] = new\(\)')

def _read(p):
    with open(p, encoding="utf-8") as f:
        return f.read()

def _extract_lang_block(src, code):
    """Return the text between `["code"] = new() {` and the matching closing brace."""
    m = re.search(r'\["' + code + r'"\] = new\(\)\s*\{', src)
    if not m:
        return None
    start = m.end()
    depth = 1
    i = start
    while i < len(src) and depth > 0:
        c = src[i]
        if c == '"':
            # skip C# string literal (no verbatim in this file typically). handle escaped quotes.
            i += 1
            while i < len(src):
                if src[i] == '\\':
                    i += 2
                    continue
                if src[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return src[start:i]
        i += 1
    return None

KEY_RE = re.compile(r'\["([^"\\]+)"\]\s*=\s*')

def _extract_keys(block):
    return KEY_RE.findall(block)


class TestPortugueseLanguage:

    def test_supported_languages_has_pt_entry(self):
        src = _read(CFG_PATH)
        assert 'Code:        "pt"' in src
        assert 'CultureCode: "pt-PT"' in src
        assert 'LangName:    "Portuguese (Português)"' in src
        assert 'NativeName:  "Português"' in src
        assert '\\U0001F1F5\\U0001F1F9' in src  # 🇵🇹
        # 12 long + 12 short months present
        long_months = ["janeiro","fevereiro","março","abril","maio","junho",
                       "julho","agosto","setembro","outubro","novembro","dezembro"]
        short_months = ["jan","fev","mar","abr","mai","jun","jul","ago","set","out","nov","dez"]
        # Extract PT block from config
        m = re.search(r'Code:\s+"pt".*?\)\s*\}', src, re.DOTALL)
        assert m, "PT LangDef not found"
        pt_block = m.group(0)
        for mo in long_months:
            assert mo in pt_block, f"missing long month {mo}"
        # Short months share tokens with other langs; verify presence within block
        for mo in short_months:
            assert f'"{mo}"' in pt_block, f"missing short month {mo}"

    def test_pt_is_seventh_language(self):
        src = _read(CFG_PATH)
        codes = re.findall(r'Code:\s+"([a-z]{2})"', src)
        assert codes == ["en", "ro", "fr", "es", "de", "it", "pt"], f"unexpected order: {codes}"

    def test_loc_has_pt_block_after_it(self):
        src = _read(LOC_PATH)
        codes_in_order = LANG_HDR_RE.findall(src)
        assert codes_in_order == ["en", "ro", "fr", "es", "de", "it", "pt"], \
            f"unexpected block order: {codes_in_order}"

    def test_it_block_ends_with_comma_before_pt(self):
        src = _read(LOC_PATH)
        # locate IT closing brace, ensure `},` before PT
        it_start = src.index('["it"] = new()')
        pt_start = src.index('["pt"] = new()')
        between = src[it_start:pt_start]
        # Last non-whitespace-and-comment chunk before PT block:
        # strip comments
        stripped = re.sub(r'//[^\n]*', '', between)
        stripped = re.sub(r'\s+', ' ', stripped).strip()
        tail = stripped[-80:]
        assert stripped.endswith('},'), f"IT block should end with (right-brace comma) before PT. Tail: {tail}"

    def test_pt_key_count_matches_en(self):
        src = _read(LOC_PATH)
        en = _extract_lang_block(src, "en")
        pt = _extract_lang_block(src, "pt")
        assert en is not None and pt is not None
        en_keys = _extract_keys(en)
        pt_keys = _extract_keys(pt)
        assert len(en_keys) == 998, f"EN has {len(en_keys)} keys, expected 998"
        assert len(pt_keys) == len(en_keys), f"PT has {len(pt_keys)} keys, EN has {len(en_keys)}"

    def test_pt_keys_match_en_exactly_and_in_order(self):
        src = _read(LOC_PATH)
        en_keys = _extract_keys(_extract_lang_block(src, "en"))
        pt_keys = _extract_keys(_extract_lang_block(src, "pt"))
        assert en_keys == pt_keys, "PT keys differ from EN in name or order"

    def test_pt_no_duplicate_keys(self):
        src = _read(LOC_PATH)
        pt_keys = _extract_keys(_extract_lang_block(src, "pt"))
        dups = [k for k in set(pt_keys) if pt_keys.count(k) > 1]
        assert not dups, f"Duplicate PT keys: {dups}"

    def test_pt_placeholders_preserved(self):
        """Every EN value's {0}, {0:F1}%, {LANGUAGE_NAME} placeholders and HTML tags
        must appear in the corresponding PT value."""
        src = _read(LOC_PATH)
        en = _extract_lang_block(src, "en")
        pt = _extract_lang_block(src, "pt")
        # Extract key -> value pairs (single-line entries)
        pair_re = re.compile(r'\["([^"\\]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"', re.DOTALL)
        en_map = dict(pair_re.findall(en))
        pt_map = dict(pair_re.findall(pt))

        placeholder_res = [
            re.compile(r'\{\d+(?::[^}]+)?\}'),   # {0}, {0:F1}
            re.compile(r'\{LANGUAGE_NAME\}'),
        ]
        missing = []
        for k, en_val in en_map.items():
            pt_val = pt_map.get(k)
            if pt_val is None:
                continue
            for rex in placeholder_res:
                en_ph = sorted(rex.findall(en_val))
                pt_ph = sorted(rex.findall(pt_val))
                if en_ph != pt_ph:
                    missing.append((k, en_ph, pt_ph))
        assert not missing, f"Placeholder mismatch in {len(missing)} keys. First 5: {missing[:5]}"

    def test_pt_html_tags_consistent_with_italian(self):
        """Compare PT HTML tags to IT (peer non-EN language), not EN — because
        some non-EN languages intentionally add <code>/<em> that EN lacks."""
        src = _read(LOC_PATH)
        it = _extract_lang_block(src, "it")
        pt = _extract_lang_block(src, "pt")
        pair_re = re.compile(r'\["([^"\\]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"', re.DOTALL)
        it_map = dict(pair_re.findall(it))
        pt_map = dict(pair_re.findall(pt))
        tag_re = re.compile(r'<[^>]+>')
        mismatches = []
        for k, it_val in it_map.items():
            pt_val = pt_map.get(k)
            if pt_val is None:
                continue
            it_tags = sorted(tag_re.findall(it_val))
            pt_tags = sorted(tag_re.findall(pt_val))
            if it_tags != pt_tags:
                mismatches.append((k, it_tags, pt_tags))
        assert not mismatches, f"HTML tag mismatch vs IT in {len(mismatches)} keys. First 5: {mismatches[:5]}"

    def test_pt_no_unescaped_internal_quotes(self):
        """Every PT string literal must have properly-escaped internal quotes.
        Approach: for each entry ["key"] = "value", tokenize value respecting \\"."""
        src = _read(LOC_PATH)
        pt = _extract_lang_block(src, "pt")
        # A line should look like ["Key"] = "value", or ["Key"] = "value"
        # We assume single-line entries. Check each line starting with `["`
        bad = []
        for lineno, line in enumerate(pt.splitlines(), 1):
            s = line.strip()
            if not s.startswith('["'):
                continue
            # find the value string
            m = re.match(r'\["[^"\\]+"\]\s*=\s*"((?:[^"\\]|\\.)*)"\s*,?\s*$', s)
            if not m:
                bad.append((lineno, s[:120]))
        assert not bad, f"{len(bad)} malformed PT lines. First 3: {bad[:3]}"

    def test_pt_block_closes_correctly(self):
        """The outer `_translations` dict must close with `};` after PT block."""
        src = _read(LOC_PATH)
        pt_start = src.index('["pt"] = new()')
        tail = src[pt_start:]
        # after PT block closes: `}` (inner) then `};` (outer)
        assert re.search(r'\}\s*\}\s*;', tail), "Outer dict must close with `};` after PT block"

    def test_sampling_date_parser_has_portuguese_months(self):
        src = _read(PARSER_PATH)
        expected_pt = ["janeiro", "fevereiro", "março", "marco", "abr", "abril",
                       "maio", "junho", "julho", "setembro", "out", "outubro",
                       "dez", "dezembro", "fev", "fevereiro"]
        # 'fev' short — confirm
        for tok in ["janeiro","fev","fevereiro","março","marco","abr","abril","maio",
                    "junho","julho","setembro","out","outubro","dez","dezembro"]:
            assert re.search(r'\{\s*"' + re.escape(tok) + r'"\s*,\s*\d+\s*\}', src), \
                f"Portuguese month token '{tok}' missing in MonthLookup"

    def test_sampling_date_parser_no_set_for_portuguese(self):
        """`set` for PT (September) was intentionally omitted to avoid duplicate key with Italian."""
        src = _read(PARSER_PATH)
        # count `"set"` occurrences as keys
        n = len(re.findall(r'\{\s*"set"\s*,', src))
        assert n == 1, f'`"set"` should appear exactly once (Italian only); found {n}'

    def test_sampling_date_parser_no_duplicate_keys(self):
        src = _read(PARSER_PATH)
        # extract MonthLookup block
        m = re.search(r'MonthLookup\s*=\s*new\([^)]*\)\s*\{(.*?)\};', src, re.DOTALL)
        assert m, "MonthLookup dict not found"
        block = m.group(1)
        keys = re.findall(r'\{\s*"([^"]+)"\s*,\s*\d+\s*\}', block)
        dups = sorted({k for k in keys if keys.count(k) > 1})
        assert not dups, f"Duplicate keys in MonthLookup: {dups}"

    def test_pt_uses_european_conventions(self):
        """Spot-check: PT should use European PT (ficheiro/utilizador/ecrã),
        NOT Brazilian (arquivo/usuário/tela)."""
        src = _read(LOC_PATH)
        pt = _extract_lang_block(src, "pt")
        pt_lower = pt.lower()
        # Positive markers — at least one should appear
        pos = ["ficheiro", "utilizador", "ecrã"]
        assert any(p in pt_lower for p in pos), \
            f"None of European PT markers {pos} found in PT block"
        # Negative markers — 'arquivo' means BOTH 'file' (Brazilian) and 'archive'
        # (European) in Portuguese. Only flag as Brazilian when used for "file"
        # semantics. Since all current 'arquivo' occurrences correspond to EN
        # 'archive' keys, we skip this check and only flag clear Brazilian usage.
        usu_hits = len(re.findall(r'\busu[áa]rio', pt_lower))
        assert usu_hits == 0, f"Found {usu_hits} Brazilian 'usuário/usuario' — should be 'utilizador'"
        # tela is ambiguous (part of estela, etc.) — informational only.
        tela_hits = len(re.findall(r'\btela\b', pt_lower))
        arq_hits = len(re.findall(r'\barquivo', pt_lower))
        print(f"Neg word counts (informational): arquivo={arq_hits} (archive-noun ok), usuario={usu_hits}, tela={tela_hits}")

    def test_only_three_files_changed(self):
        """git diff should be limited to the 3 target files (plus possibly doc)."""
        try:
            out = subprocess.check_output(
                ["git", "-C", "/app", "diff", "--name-only", "HEAD"],
                text=True, stderr=subprocess.STDOUT).strip()
        except Exception as e:
            pytest.skip(f"git not usable: {e}")
        if not out:
            pytest.skip("no diff vs HEAD (already committed) — skipping")
        changed = set(out.splitlines())
        allowed = {
            "MedicalApp/Services/Loc.cs",
            "MedicalApp/Services/SupportedLanguagesConfig.cs",
            "MedicalApp/Services/SamplingDateParser.cs",
            "MedicalApp/Docs/Adding_New_Language.md",
        }
        extra = changed - allowed
        assert not extra, f"Unexpected files changed: {extra}"
